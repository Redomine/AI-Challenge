using System.Text.Json;
using DesignerAssistant.Agent;
using DesignerAssistant.Models;
using DesignerAssistant.Scheduling;

namespace DesignerAssistant.Web.Services;

public sealed class ScheduledTaskService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<ScheduledTaskService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _storagePath;
    private List<ScheduledAgentTask> _tasks = [];

    public ScheduledTaskService(
        IHttpClientFactory httpClientFactory,
        IWebHostEnvironment environment,
        ILogger<ScheduledTaskService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _environment = environment;
        _logger = logger;
        _storagePath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "scheduled-tasks.json"));
        Load();
    }

    public event Action? Changed;

    public async Task<IReadOnlyList<ScheduledAgentTask>> GetTasksAsync()
    {
        await _gate.WaitAsync();
        try { return _tasks.OrderBy(task => task.NextRunAt).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(ScheduledAgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (string.IsNullOrWhiteSpace(task.Name)) throw new ArgumentException("Укажите название задачи.");
        if (string.IsNullOrWhiteSpace(task.Prompt)) throw new ArgumentException("Укажите текст задачи.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var normalized = task with
            {
                Name = task.Name.Trim(),
                Prompt = task.Prompt.Trim(),
                DayOfMonth = task.Period == SchedulePeriod.Monthly ? Math.Clamp(task.DayOfMonth ?? 1, 1, 31) : null,
                DayOfWeek = task.Period == SchedulePeriod.Weekly ? task.DayOfWeek ?? System.DayOfWeek.Monday : null,
                NextRunAt = ScheduleCalculator.GetNextRun(task.Period, task.Time, task.DayOfWeek, task.DayOfMonth, DateTimeOffset.Now, TimeZoneInfo.Local),
                IsRunning = false,
                OperationTimeoutMinutes = Math.Clamp(task.OperationTimeoutMinutes, 1, 120)
            };
            var index = _tasks.FindIndex(item => item.Id == normalized.Id);
            if (index < 0) _tasks.Add(normalized);
            else _tasks[index] = normalized;
            await PersistAsync(cancellationToken);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _tasks.RemoveAll(task => task.Id == id);
            await PersistAsync(cancellationToken);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    public async Task RunNowAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ScheduledAgentTask task;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            task = _tasks.FirstOrDefault(item => item.Id == id)
                ?? throw new InvalidOperationException("Периодическая задача не найдена.");
            if (task.IsRunning) return;
            Replace(task with { IsRunning = true, LastResult = "Выполняется..." });
            await PersistAsync(cancellationToken);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
        await ExecuteTaskAsync(task, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var due = await GetDueTasksAsync(stoppingToken);
                foreach (var task in due) await RunNowAsync(task.Id, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Ошибка цикла периодических задач");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    private async Task ExecuteTaskAsync(ScheduledAgentTask scheduledTask, CancellationToken cancellationToken)
    {
        var finishedAt = DateTimeOffset.Now;
        string result;
        try
        {
            await using var session = new AssistantSession(_httpClientFactory);
            await session.InitializeAsync(
                _environment.ContentRootPath,
                allowInteractiveConfirmation: false,
                autoApproveRevitChanges: scheduledTask.AutoApproveRevitChanges,
                operationTimeoutMinutes: scheduledTask.OperationTimeoutMinutes,
                cancellationToken: cancellationToken);
            await session.StartDirectTaskAsync(scheduledTask.Prompt, new TaskPauseOptions(false, false, false), cancellationToken);
            var context = session.CurrentTask;
            result = context?.State == TaskState.Done
                ? context.ValidationResult ?? context.ExecutionResult ?? "Задача выполнена."
                : $"Остановлено на этапе {context?.State}: {context?.FailureReason ?? context?.ValidationResult ?? context?.ExecutionResult}";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Ошибка периодической задачи {TaskId}", scheduledTask.Id);
            result = $"Ошибка: {exception.Message}";
        }

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            var current = _tasks.FirstOrDefault(item => item.Id == scheduledTask.Id);
            if (current is null) return;
            Replace(current with
            {
                IsRunning = false,
                LastRunAt = finishedAt,
                LastResult = result,
                NextRunAt = ScheduleCalculator.GetNextRun(current.Period, current.Time, current.DayOfWeek, current.DayOfMonth, finishedAt, TimeZoneInfo.Local)
            });
            await PersistAsync(CancellationToken.None);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    private async Task<IReadOnlyList<ScheduledAgentTask>> GetDueTasksAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return _tasks.Where(task => task.IsEnabled && !task.IsRunning && task.NextRunAt <= DateTimeOffset.Now).ToArray(); }
        finally { _gate.Release(); }
    }

    private void Replace(ScheduledAgentTask task)
    {
        var index = _tasks.FindIndex(item => item.Id == task.Id);
        if (index >= 0) _tasks[index] = task;
    }

    private void Load()
    {
        if (!File.Exists(_storagePath)) return;
        try
        {
            _tasks = JsonSerializer.Deserialize<List<ScheduledAgentTask>>(File.ReadAllText(_storagePath), JsonOptions) ?? [];
            _tasks = _tasks.Select(task => task with
            {
                IsRunning = false,
                OperationTimeoutMinutes = task.OperationTimeoutMinutes <= 0 ? 10 : Math.Clamp(task.OperationTimeoutMinutes, 1, 120)
            }).ToList();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Не удалось прочитать {StoragePath}", _storagePath);
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        var temporaryPath = _storagePath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(_tasks, JsonOptions), cancellationToken);
        File.Move(temporaryPath, _storagePath, true);
    }
}
