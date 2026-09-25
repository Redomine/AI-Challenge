using System.Text.Json;
using DesignerAssistant.Models;
using DesignerAssistant.Tools;

namespace DesignerAssistant.Web.Services;

public sealed class TaskOperationsReporter(StdioMcpToolProvider log, StdioMcpToolProvider ops, bool automaticNotifications = true)
{
    private string _taskId = Guid.NewGuid().ToString("N");
    private TaskState? _lastState;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _taskId = Guid.NewGuid().ToString("N");
        _lastState = null;
        await RecordAsync("task_started", "Task started", true, cancellationToken);
    }

    public async Task ObserveAsync(TaskState? state, CancellationToken cancellationToken)
    {
        if (state is null || state == _lastState) return;
        _lastState = state;
        await RecordAsync("task_state", state.ToString()!, state == TaskState.Done, cancellationToken);
        if (!automaticNotifications || state is not (TaskState.Done or TaskState.Failed or TaskState.ExecutionInterrupted)) return;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_MAIL_TO"))) return;
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            subject = $"BIM Coordinator: {state}",
            body = $"Task {_taskId} reached state {state} at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}. Check the assistant journal for details."
        }));
        var result = await ops.InvokeAsync("ops_send_notification", arguments.RootElement, cancellationToken);
        using var response = JsonDocument.Parse(result);
        var ok = response.RootElement.TryGetProperty("ok", out var status) && status.GetBoolean();
        await RecordAsync("notification", ok ? "delivered" : "failed", ok, cancellationToken);
    }

    public async Task<bool> ReportScheduledOutcomeAsync(
        string taskName, string summary, bool success, bool notifyOnSuccess, CancellationToken cancellationToken)
    {
        var concise = summary.Length > 1600 ? summary[..1600] : summary;
        await RecordAsync("scheduled_task_result", $"{taskName}: {concise}", success, cancellationToken);
        if (!success || !notifyOnSuccess) return false;
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            subject = $"BIM Coordinator: {taskName} выполнена",
            body = $"{concise}\nВремя: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}."
        }));
        var result = await ops.InvokeAsync("ops_send_notification", arguments.RootElement, cancellationToken);
        using var response = JsonDocument.Parse(result);
        var delivered = response.RootElement.TryGetProperty("ok", out var status) && status.GetBoolean();
        await RecordAsync("notification", delivered ? "delivered" : "failed", delivered, cancellationToken);
        return delivered;
    }

    private async Task RecordAsync(string type, string summary, bool ok, CancellationToken cancellationToken)
    {
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            taskId = _taskId, eventType = type, source = "assistant", summary, ok, durationMs = 0
        }));
        await log.InvokeAsync("log_append_event", arguments.RootElement, cancellationToken);
    }
}
