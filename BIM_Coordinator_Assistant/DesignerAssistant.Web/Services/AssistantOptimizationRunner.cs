using System.Diagnostics;
using System.Text.Json;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;

namespace DesignerAssistant.Web.Services;

public sealed record AssistantOptimizationStep(
    string Name, bool Passed, long ElapsedMilliseconds, string? Error, string? ToolResult,
    bool ToolInvocationStarted = false);

public sealed record AssistantOptimizationRun(
    string Profile, IReadOnlyList<AssistantOptimizationStep> Steps,
    long ElapsedMilliseconds, long? PeakGpuMemoryMiB, long? PeakOllamaRamMiB)
{
    public bool Passed => Steps.Count == 2 && Steps.All(step => step.Passed);
}

public sealed record AssistantOptimizationReport(
    string ModelPath, DateTimeOffset StartedAt, IReadOnlyList<AssistantOptimizationRun> Runs);

public sealed class AssistantOptimizationRunner(IHttpClientFactory httpClientFactory, IWebHostEnvironment environment)
{
    private const string OpenTool = "revit_custom_open_model";
    private const string SyncCloseTool = "revit_custom_sync_relinquish_and_close";
    public const string SyncCloseQuestion =
        "Локальная модель уже открыта в активном Revit. Не открывай модель повторно и не передавай path. " +
        "Синхронизируй её с центральной, освободи права и закрой через revit_custom_sync_relinquish_and_close. " +
        "План должен содержать только этот инструмент, затем сообщи его фактический результат.";

    public async Task<AssistantOptimizationReport> RunAsync(
        string modelPath,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(modelPath);
        if (!File.Exists(fullPath) || !fullPath.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Укажите существующий RVT-файл для живого теста.", fullPath);
        var started = DateTimeOffset.Now;
        var runs = new List<AssistantOptimizationRun>(2);
        foreach (var profile in new[] { OllamaTuningProfile.Standard, OllamaTuningProfile.Optimized })
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"{profile}: подключаю живой агент и Revit...");
            runs.Add(await RunProfileAsync(profile, fullPath, progress, cancellationToken));
            if (!runs[^1].Passed &&
                runs[^1].Steps.Any(step => step.ToolInvocationStarted || step.Name == "Синхронизация и закрытие"))
                break;
        }
        return new AssistantOptimizationReport(fullPath, started, runs);
    }

    private async Task<AssistantOptimizationRun> RunProfileAsync(
        OllamaTuningProfile profile, string modelPath,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var steps = new List<AssistantOptimizationStep>(2);
        using var sampler = new ResourceSampler();
        sampler.Start();
        var databasePath = Path.Combine(Path.GetTempPath(), "AIChallengeDay29", Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        try
        {
            var authorizedTool = OpenTool;
            var targetProcessId = 0;
            Task<bool> ConfirmTestOperation(string proposal) => Task.FromResult(
                IsAutoConfirmableProposal(proposal, authorizedTool, modelPath, targetProcessId));
            await using var session = new AssistantSession(httpClientFactory);
            await session.InitializeAsync(environment.ContentRootPath,
                allowInteractiveConfirmation: false, automaticNotifications: false,
                cancellationToken: cancellationToken, tuningProfile: profile,
                databasePathOverride: databasePath, confirmationHandler: ConfirmTestOperation);
            session.SelectProvider(LlmProvider.Ollama);
            var sessions = await session.GetRevitSessionsAsync(cancellationToken);
            var target = sessions.SingleOrDefault(item => item.Year == 2022 && item.IsRoutable)
                ?? throw new InvalidOperationException("Нет однозначного доступного сеанса Revit 2022 в rvt-mcp.");
            var modelName = Path.GetFileNameWithoutExtension(modelPath);
            if (target.ProjectName.Contains(modelName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("В Revit уже открыта локальная копия тестовой модели. Закройте её перед сравнением профилей.");
            await session.SelectRevitSessionAsync(target.ProcessId.ToString(), cancellationToken);
            targetProcessId = target.ProcessId;

            progress?.Report($"{profile}: открытие модели...");
            var open = await RunTaskAsync(session, "Открытие",
                $"Открой модель \"{modelPath}\" без отсоединения. Подтверди результат инструментом.",
                OpenTool, modelPath, cancellationToken);
            steps.Add(open);
            if (open.Passed)
            {
                authorizedTool = SyncCloseTool;
                progress?.Report($"{profile}: синхронизация и закрытие...");
                var sync = await RunTaskAsync(session, "Синхронизация и закрытие",
                    SyncCloseQuestion,
                    SyncCloseTool, null, cancellationToken);
                if (sync.Passed)
                {
                    var closed = await WaitForCloseAsync(target.ProcessId, modelPath, cancellationToken);
                    if (!closed) sync = sync with { Passed = false, Error = "Команда Close отправлена, но закрытие модели не подтверждено окном Revit." };
                }
                steps.Add(sync);
                if (!sync.Passed && CanFallbackAfterAgentFailure(session))
                {
                    progress?.Report($"{profile}: проверяю модель перед резервной синхронизацией и закрытием...");
                    if (await IsTestModelOpenAsync(session, target.ProcessId, modelPath, cancellationToken))
                    {
                        progress?.Report($"{profile}: резервная синхронизация и закрытие через MCP...");
                        steps.Add(await RunFallbackSyncCloseAsync(session, target.ProcessId, modelPath, cancellationToken));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            steps.Add(new AssistantOptimizationStep(steps.Count == 0 ? "Подключение" : "Прогон",
                false, timer.ElapsedMilliseconds, exception.Message, null));
        }
        await sampler.StopAsync();
        return new AssistantOptimizationRun(profile.ToString(), steps, timer.ElapsedMilliseconds,
            sampler.PeakGpuMemoryMiB, sampler.PeakOllamaRamMiB);
    }

    public static bool IsAutoConfirmableProposal(
        string proposal, string expectedTool, string modelPath, int processId)
    {
        if (processId <= 0 || expectedTool is not (OpenTool or SyncCloseTool)) return false;
        var lines = proposal.Split('\n', 3);
        if (lines.Length != 3 || lines[0] != $"Инструмент: {expectedTool}" ||
            !lines[1].EndsWith($", ID {processId}", StringComparison.Ordinal) ||
            !lines[2].StartsWith("Аргументы: ", StringComparison.Ordinal)) return false;
        try
        {
            using var document = JsonDocument.Parse(lines[2]["Аргументы: ".Length..]);
            var arguments = document.RootElement;
            if (arguments.ValueKind != JsonValueKind.Object) return false;
            if (expectedTool == SyncCloseTool)
                return arguments.EnumerateObject().All(property =>
                    property.Name == "comment" && property.Value.ValueKind == JsonValueKind.String);
            if (!arguments.TryGetProperty("path", out var path) ||
                path.ValueKind != JsonValueKind.String ||
                !string.Equals(path.GetString(), modelPath, StringComparison.Ordinal)) return false;
            foreach (var property in arguments.EnumerateObject())
            {
                var valid = property.Name switch
                {
                    "path" => true,
                    "detach" or "unloadLinksAfterOpen" => property.Value.ValueKind == JsonValueKind.False,
                    "worksetMode" => property.Value.ValueKind == JsonValueKind.String &&
                                     property.Value.GetString() == "all",
                    "worksetNames" => property.Value.ValueKind == JsonValueKind.Array &&
                                      property.Value.GetArrayLength() == 0,
                    _ => false
                };
                if (!valid) return false;
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool CanFallbackAfterAgentFailure(AssistantSession session)
    {
        var result = session.CurrentTask?.ToolResults?.FirstOrDefault(item => item.Tool == SyncCloseTool);
        return result is null;
    }

    private static async Task<AssistantOptimizationStep> RunFallbackSyncCloseAsync(
        AssistantSession session, int processId, string modelPath, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var result = await session.RunConfirmedRevitToolAsync(SyncCloseTool,
                JsonSerializer.SerializeToElement(new { comment = "AI Challenge Day 29: test cleanup" }), cancellationToken);
            var raw = result.ToJson();
            if (!result.Ok || !result.Completed ||
                !HasBoolean(result.Result, "synchronized") || !HasBoolean(result.Result, "closePosted"))
                return new AssistantOptimizationStep("Резервная синхронизация и закрытие", false,
                    timer.ElapsedMilliseconds, result.Error?.Message ?? "MCP не подтвердил синхронизацию и команду Close.", raw,
                    result.Confirmed);
            if (!await WaitForCloseAsync(processId, modelPath, cancellationToken))
                return new AssistantOptimizationStep("Резервная синхронизация и закрытие", false,
                    timer.ElapsedMilliseconds, "Команда Close отправлена, но модель осталась открытой.", raw, true);
            return new AssistantOptimizationStep("Резервная синхронизация и закрытие", true,
                timer.ElapsedMilliseconds, null, raw, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            return new AssistantOptimizationStep("Резервная синхронизация и закрытие", false,
                timer.ElapsedMilliseconds, exception.Message, null);
        }
    }

    private static async Task<bool> IsTestModelOpenAsync(
        AssistantSession session, int processId, string modelPath, CancellationToken cancellationToken)
    {
        var modelName = Path.GetFileNameWithoutExtension(modelPath);
        var sessions = await session.GetRevitSessionsAsync(cancellationToken);
        return sessions.Any(item => item.ProcessId == processId && item.IsRoutable &&
            item.ProjectName.Contains(modelName, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<AssistantOptimizationStep> RunTaskAsync(
        AssistantSession session, string name, string question, string expectedTool,
        string? expectedPath, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var toolInvocationStarted = false;
        try
        {
            await session.StartTaskAsync(question, new TaskPauseOptions(true, false, false), cancellationToken);
            var plan = session.CurrentTask?.StructuredPlan;
            if (!session.AwaitingPlanApproval || plan is null)
                return Failed($"План не готов: {session.CurrentTask?.State}; {session.CurrentTask?.FailureReason}");
            var planError = ValidatePlan(plan, expectedTool, expectedPath);
            if (planError is not null) return Failed(planError);

            toolInvocationStarted = true;
            await session.ApprovePlanAsync(cancellationToken);
            var context = session.CurrentTask;
            var result = context?.ToolResults?.FirstOrDefault(item => item.Tool == expectedTool);
            var raw = result?.ToJson();
            if (result is null || !result.Ok || !result.Completed)
                return Failed($"Нет успешного завершённого результата {expectedTool}: {result?.Error?.Message ?? context?.FailureReason}", raw);
            if (expectedTool == SyncCloseTool &&
                (!HasBoolean(result.Result, "synchronized") || !HasBoolean(result.Result, "closePosted")))
                return Failed("Инструмент не подтвердил synchronized=true и closePosted=true.", raw);
            if (context?.State != TaskState.Done)
                return Failed($"Агент не завершил задачу: {context?.State}; {context?.FailureReason}", raw);
            return new AssistantOptimizationStep(name, true, timer.ElapsedMilliseconds, null, raw, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { return Failed(exception.Message); }

        AssistantOptimizationStep Failed(string error, string? result = null) =>
            new(name, false, timer.ElapsedMilliseconds, error, result, toolInvocationStarted);
    }

    public static string? ValidatePlan(TaskPlan plan, string expectedTool, string? expectedPath)
    {
        if (plan.Steps.Count != 1 || plan.Steps[0].Tool != expectedTool)
            return $"План должен содержать ровно {expectedTool}; получено: {string.Join(", ", plan.Steps.Select(step => step.Tool))}.";
        if (expectedPath is not null &&
            (!plan.Steps[0].Arguments.TryGetValue("path", out var path) ||
             path.ValueKind != JsonValueKind.String ||
             !string.Equals(path.GetString(), expectedPath, StringComparison.Ordinal)))
            return "План изменил путь к модели или не передал его в аргументе path.";
        return null;
    }

    public static bool HasBoolean(JsonElement? result, string name)
    {
        if (result is not { ValueKind: JsonValueKind.Object } root) return false;
        if (root.TryGetProperty(name, out var value)) return value.ValueKind == JsonValueKind.True;
        foreach (var property in root.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Object && HasBoolean(property.Value, name)) return true;
        return false;
    }

    private static async Task<bool> WaitForCloseAsync(int processId, string modelPath, CancellationToken cancellationToken)
    {
        var modelName = Path.GetFileNameWithoutExtension(modelPath);
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            try
            {
                using var process = Process.GetProcessById(processId);
                var title = process.MainWindowTitle;
                if (title.Contains("Autodesk Revit", StringComparison.OrdinalIgnoreCase) &&
                    !title.Contains(modelName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (ArgumentException) { return false; }
        }
        return false;
    }

    private sealed class ResourceSampler : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private Task? _task;
        public long? PeakGpuMemoryMiB { get; private set; }
        public long? PeakOllamaRamMiB { get; private set; }

        public void Start() => _task = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                SampleRam();
                await SampleGpuAsync();
                try { await Task.Delay(2000, _stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        });

        public async Task StopAsync()
        {
            _stop.Cancel();
            if (_task is not null) await _task;
        }

        private void SampleRam()
        {
            long bytes = 0;
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                    try { if (process.ProcessName.Contains("ollama", StringComparison.OrdinalIgnoreCase)) bytes += process.WorkingSet64; }
                    catch (InvalidOperationException) { }
            }
            if (bytes > 0) PeakOllamaRamMiB = Math.Max(PeakOllamaRamMiB ?? 0, bytes / 1024 / 1024);
        }

        private async Task SampleGpuAsync()
        {
            try
            {
                using var process = new Process { StartInfo = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true } };
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync(_stop.Token);
                if (process.ExitCode == 0 && long.TryParse(output.Trim(), out var memory))
                    PeakGpuMemoryMiB = Math.Max(PeakGpuMemoryMiB ?? 0, memory);
            }
            catch (Exception exception) when (exception is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }

        public void Dispose() => _stop.Dispose();
    }
}
