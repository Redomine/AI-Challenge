using System.ComponentModel;
using System.Net;
using System.Net.Mail;
using ModelContextProtocol.Server;

public sealed class OpsState
{
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    public object Heartbeat() => new { ok = true, status = "running", startedAt = _startedAt, checkedAt = DateTimeOffset.UtcNow };

    public async Task<object> SendNotificationAsync(string subject, string body, CancellationToken cancellationToken)
    {
        var host = Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_SMTP_HOST");
        var recipient = Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_MAIL_TO");
        var sender = Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_MAIL_FROM");
        var user = Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_SMTP_USER");
        var password = Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_SMTP_PASSWORD");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(recipient) || string.IsNullOrWhiteSpace(sender))
            return new { ok = false, error = "SMTP host, sender or recipient is not configured." };
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
            return new { ok = false, error = "SMTP username or app password is not configured." };
        if (subject.Length > 200 || body.Length > 4000) throw new ArgumentException("Mail content exceeds size limit.");
        using var message = new MailMessage(sender, recipient, subject, body);
        using var client = new SmtpClient(host,
            int.TryParse(Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_SMTP_PORT"), out var port) ? port : 587)
        {
            EnableSsl = true,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };
        client.Credentials = new NetworkCredential(user, password);
        try
        {
            await client.SendMailAsync(message, cancellationToken);
            return new { ok = true, deliveredTo = recipient };
        }
        catch (SmtpException exception)
        {
            return new { ok = false, error = exception.Message };
        }
    }
}

[McpServerToolType]
public sealed class OpsTools(OpsState state)
{
    [McpServerTool(Name = "ops_heartbeat", ReadOnly = true)]
    [Description("Return the current operational server heartbeat and uptime origin.")]
    public object Heartbeat() => state.Heartbeat();

    [McpServerTool(Name = "ops_send_notification")]
    [Description("Send a task status notification to the fixed recipient configured on the server. Never accepts a recipient from the model.")]
    public Task<object> SendNotification(string subject, string body, CancellationToken cancellationToken) =>
        state.SendNotificationAsync(subject, body, cancellationToken);
}
