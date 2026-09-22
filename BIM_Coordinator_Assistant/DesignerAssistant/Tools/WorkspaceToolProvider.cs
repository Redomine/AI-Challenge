using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using DesignerAssistant.Models;

namespace DesignerAssistant.Tools;

public sealed class WorkspaceToolProvider : IToolProvider
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".idea", ".vs", "bin", "obj", ".verify-build", "node_modules"
    };
    private readonly WorkspacePathPolicy _paths;
    private readonly ProcessRunner _runner;
    private readonly IReadOnlyList<ToolDefinition> _tools;

    public WorkspaceToolProvider(string workspaceRoot, ProcessRunner? runner = null)
    {
        _paths = new WorkspacePathPolicy(workspaceRoot);
        _runner = runner ?? new ProcessRunner();
        _tools = BuildTools();
    }

    public string WorkspaceRoot => _paths.Root;

    public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_tools);

    public async Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var result = name switch
        {
            "workspace_list_files" => ListFiles(arguments),
            "workspace_read_text_file" => ReadTextFile(arguments),
            "workspace_search_text" => SearchText(arguments),
            "workspace_path_exists" => PathExists(arguments),
            "workspace_list_processes" => ListProcesses(arguments),
            "workspace_is_port_listening" => IsPortListening(arguments),
            "workspace_dotnet_build" => await RunDotnetAsync("build", arguments, cancellationToken),
            "workspace_dotnet_test" => await RunDotnetAsync("test", arguments, cancellationToken),
            "workspace_run_command_recipe" => await RunCommandRecipeAsync(arguments, cancellationToken),
            _ => throw new InvalidOperationException($"Workspace-инструмент '{name}' не зарегистрирован.")
        };
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private object ListFiles(JsonElement arguments)
    {
        var directory = _paths.Resolve(OptionalString(arguments, "path") ?? "", mustExist: true);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Каталог не найден.");
        var pattern = OptionalString(arguments, "pattern") ?? "*";
        if (pattern.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("pattern должен содержать только маску имени файла.");
        var maxResults = BoundedInt(arguments, "maxResults", 200, 1, 1000);
        var files = EnumerateFiles(directory, pattern)
            .Take(maxResults + 1)
            .Select(_paths.ToRelative)
            .ToArray();
        return new { ok = true, root = _paths.Root, files = files.Take(maxResults), truncated = files.Length > maxResults };
    }

    private object ReadTextFile(JsonElement arguments)
    {
        var path = _paths.ResolveReadableFile(RequiredString(arguments, "path"));
        var maxBytes = BoundedInt(arguments, "maxBytes", 200_000, 1, 1_000_000);
        var info = new FileInfo(path);
        if (info.Length > maxBytes) throw new InvalidOperationException($"Файл превышает лимит {maxBytes} байт.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Any(value => value == 0)) throw new InvalidDataException("Файл похож на бинарный и не может быть прочитан как текст.");
        var content = new UTF8Encoding(false, true).GetString(bytes);
        return new { ok = true, path = _paths.ToRelative(path), bytes = bytes.Length, content };
    }

    private object SearchText(JsonElement arguments)
    {
        var directory = _paths.Resolve(OptionalString(arguments, "path") ?? "", mustExist: true);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Каталог поиска не найден.");
        var query = RequiredString(arguments, "query");
        if (query.Length > 500) throw new ArgumentException("Поисковая строка слишком длинная.");
        var pattern = OptionalString(arguments, "pattern") ?? "*";
        if (pattern.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("pattern должен содержать только маску имени файла.");
        var maxResults = BoundedInt(arguments, "maxResults", 100, 1, 500);
        var matches = new List<object>();
        foreach (var file in EnumerateFiles(directory, pattern))
        {
            string[] lines;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 1_000_000) continue;
                _paths.ResolveReadableFile(_paths.ToRelative(file));
                lines = File.ReadAllLines(file, Encoding.UTF8);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                continue;
            }
            for (var index = 0; index < lines.Length; index++)
            {
                if (!lines[index].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                matches.Add(new { path = _paths.ToRelative(file), line = index + 1, text = lines[index].Trim() });
                if (matches.Count >= maxResults) return new { ok = true, matches, truncated = true };
            }
        }
        return new { ok = true, matches, truncated = false };
    }

    private object PathExists(JsonElement arguments)
    {
        var path = _paths.Resolve(RequiredString(arguments, "path"));
        return new { ok = true, path = _paths.ToRelative(path), file = File.Exists(path), directory = Directory.Exists(path) };
    }

    private static object ListProcesses(JsonElement arguments)
    {
        var requestedName = OptionalString(arguments, "name")?.Trim();
        var maxResults = BoundedInt(arguments, "maxResults", 50, 1, 200);
        var results = new List<object>();
        foreach (var process in Process.GetProcesses().OrderBy(item => item.ProcessName))
        {
            using (process)
            {
                if (!string.IsNullOrWhiteSpace(requestedName) &&
                    !process.ProcessName.Contains(requestedName, StringComparison.OrdinalIgnoreCase)) continue;
                DateTime? startedAt = null;
                try { startedAt = process.StartTime; } catch { }
                results.Add(new { name = process.ProcessName, processId = process.Id, startedAt });
                if (results.Count >= maxResults) break;
            }
        }
        return new { ok = true, processes = results, truncated = results.Count >= maxResults };
    }

    private static object IsPortListening(JsonElement arguments)
    {
        var port = BoundedInt(arguments, "port", 0, 1, 65535);
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(endpoint => endpoint.Port == port)
            .Select(endpoint => endpoint.ToString())
            .ToArray();
        return new { ok = true, port, listening = listeners.Length > 0, endpoints = listeners };
    }

    private async Task<object> RunDotnetAsync(string verb, JsonElement arguments, CancellationToken cancellationToken)
    {
        var project = _paths.Resolve(RequiredString(arguments, "project"), mustExist: true);
        var extension = Path.GetExtension(project);
        if (!File.Exists(project) || !extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("project должен указывать на существующий .csproj или .sln внутри workspace.");
        var configuration = OptionalString(arguments, "configuration") ?? "Debug";
        if (configuration is not ("Debug" or "Release")) throw new ArgumentException("Допустимы только Debug и Release.");
        var timeoutSeconds = BoundedInt(arguments, "timeoutSeconds", verb == "test" ? 180 : 120, 5, 600);
        var commandArguments = new List<string> { verb, project, "--configuration", configuration };
        if (OptionalBool(arguments, "noRestore", defaultValue: true)) commandArguments.Add("--no-restore");
        if (verb == "test" && OptionalString(arguments, "filter") is { Length: > 0 } filter)
        {
            if (filter.Length > 500) throw new ArgumentException("Фильтр тестов слишком длинный.");
            commandArguments.Add("--filter");
            commandArguments.Add(filter);
        }
        var result = await _runner.RunAsync(ResolveExecutable("dotnet"), commandArguments, Path.GetDirectoryName(project)!, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
        return new
        {
            ok = !result.TimedOut && result.ExitCode == 0,
            operation = $"dotnet {verb}",
            project = _paths.ToRelative(project),
            result.ExitCode,
            result.TimedOut,
            result.DurationMilliseconds,
            stdout = Redact(result.StandardOutput),
            stderr = Redact(result.StandardError)
        };
    }

    private async Task<object> RunCommandRecipeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var recipe = RequiredString(arguments, "recipe");
        var command = recipe switch
        {
            "git_status" => (ResolveExecutable("git"), (IReadOnlyCollection<string>)["status", "--short", "--branch"]),
            "git_diff_summary" => (ResolveExecutable("git"), (IReadOnlyCollection<string>)["diff", "--stat"]),
            "git_branch" => (ResolveExecutable("git"), (IReadOnlyCollection<string>)["branch", "--show-current"]),
            "dotnet_info" => (ResolveExecutable("dotnet"), (IReadOnlyCollection<string>)["--info"]),
            _ => throw new ArgumentException($"Рецепт '{recipe}' не разрешён.")
        };
        var result = await _runner.RunAsync(command.Item1, command.Item2, _paths.Root, TimeSpan.FromSeconds(30), cancellationToken);
        return new
        {
            ok = !result.TimedOut && result.ExitCode == 0,
            recipe,
            result.ExitCode,
            result.TimedOut,
            result.DurationMilliseconds,
            stdout = Redact(result.StandardOutput),
            stderr = Redact(result.StandardError)
        };
    }

    private IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory, pattern)) yield return file;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!ExcludedDirectories.Contains(Path.GetFileName(child))) pending.Push(child);
            }
        }
    }

    private static IReadOnlyList<ToolDefinition> BuildTools() =>
    [
        Tool("workspace_list_files", "Перечислить файлы внутри рабочего репозитория без чтения содержимого.", new { path = StringProperty(), pattern = StringProperty(), maxResults = IntegerProperty(1, 1000) }),
        Tool("workspace_read_text_file", "Прочитать небольшой текстовый файл UTF-8 внутри рабочего репозитория; секретные и бинарные файлы запрещены.", new { path = StringProperty(), maxBytes = IntegerProperty(1, 1_000_000) }, "path"),
        Tool("workspace_search_text", "Найти текст в небольших текстовых файлах рабочего репозитория.", new { query = StringProperty(), path = StringProperty(), pattern = StringProperty(), maxResults = IntegerProperty(1, 500) }, "query"),
        Tool("workspace_path_exists", "Проверить существование файла или каталога внутри рабочего репозитория.", new { path = StringProperty() }, "path"),
        Tool("workspace_dotnet_build", "Запустить dotnet build для конкретного проекта или solution внутри рабочего репозитория и вернуть фактический exit code, stdout и stderr.", DotnetProperties(includeFilter: false), "project"),
        Tool("workspace_dotnet_test", "Запустить dotnet test для конкретного проекта или solution внутри рабочего репозитория и вернуть фактический exit code, stdout и stderr.", DotnetProperties(includeFilter: true), "project"),
        Tool("workspace_run_command_recipe", "Запустить один из фиксированных read-only рецептов: git_status, git_diff_summary, git_branch или dotnet_info. Произвольные команды запрещены.", new { recipe = EnumProperty("git_status", "git_diff_summary", "git_branch", "dotnet_info") }, "recipe"),
        Tool("workspace_list_processes", "Проверить запущенные процессы по необязательной части имени без чтения их командных строк.", new { name = StringProperty(), maxResults = IntegerProperty(1, 200) }),
        Tool("workspace_is_port_listening", "Проверить, прослушивается ли локальный TCP-порт.", new { port = IntegerProperty(1, 65535) }, "port")
    ];

    private static ToolDefinition Tool(string name, string description, object properties, params string[] required) =>
        new(name, description, JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false, properties, required }));

    private static object DotnetProperties(bool includeFilter) => includeFilter
        ? new { project = StringProperty(), configuration = EnumProperty("Debug", "Release"), noRestore = BooleanProperty(), filter = StringProperty(), timeoutSeconds = IntegerProperty(5, 600) }
        : new { project = StringProperty(), configuration = EnumProperty("Debug", "Release"), noRestore = BooleanProperty(), timeoutSeconds = IntegerProperty(5, 600) };
    private static object StringProperty() => new { type = "string" };
    private static object BooleanProperty() => new { type = "boolean" };
    private static object IntegerProperty(int minimum, int maximum) => new { type = "integer", minimum, maximum };
    private static object EnumProperty(params string[] values) => new { type = "string", @enum = values };

    private static string RequiredString(JsonElement arguments, string name) =>
        OptionalString(arguments, name) is { Length: > 0 } value ? value : throw new ArgumentException($"Не задан обязательный аргумент '{name}'.");
    private static string? OptionalString(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
    private static bool OptionalBool(JsonElement arguments, string name, bool defaultValue) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;
    private static int BoundedInt(JsonElement arguments, string name, int defaultValue, int minimum, int maximum)
    {
        var result = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : defaultValue;
        return result >= minimum && result <= maximum ? result : throw new ArgumentOutOfRangeException(name, $"Значение должно быть от {minimum} до {maximum}.");
    }

    private static string Redact(string value)
    {
        var secret = Environment.GetEnvironmentVariable("GIGACHAT_AUTH_KEY");
        return string.IsNullOrWhiteSpace(secret) ? value : value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
    }

    private static string ResolveExecutable(string name)
    {
        var configured = name == "dotnet" ? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") : null;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidate = name == "dotnet"
            ? Path.Combine(programFiles, "dotnet", "dotnet.exe")
            : Path.Combine(programFiles, "Git", "cmd", "git.exe");
        return File.Exists(candidate) ? candidate : throw new FileNotFoundException($"Не найден разрешённый исполняемый файл {name}.", candidate);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
