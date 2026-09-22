using System.Diagnostics;

namespace DesignerAssistant.Tools;

public sealed record ProcessExecutionResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    long DurationMilliseconds);

public sealed class ProcessRunner(int maxOutputCharacters = 1_000_000)
{
    public async Task<ProcessExecutionResult> RunAsync(
        string executable,
        IReadOnlyCollection<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var key in startInfo.Environment.Keys
                     .Where(IsSensitiveEnvironmentVariable)
                     .ToArray())
            startInfo.Environment.Remove(key);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        var watch = Stopwatch.StartNew();
        if (!process.Start()) throw new InvalidOperationException($"Не удалось запустить {executable}.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        watch.Stop();
        var output = Limit(await outputTask);
        var error = Limit(await errorTask);
        return new ProcessExecutionResult(timedOut ? null : process.ExitCode, output, error, timedOut, watch.ElapsedMilliseconds);
    }

    private string Limit(string value) => value.Length <= maxOutputCharacters
        ? value
        : value[..maxOutputCharacters] + $"\n[OUTPUT_TRUNCATED originalCharacters={value.Length}]";

    private static bool IsSensitiveEnvironmentVariable(string name) =>
        name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase);
}
