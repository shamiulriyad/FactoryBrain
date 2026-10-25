using System.Net;
using System.Net.Mail;
using System.Threading.Channels;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Auth;

namespace FactoryBrain.Infrastructure.Services;

/// <summary>
/// Outgoing email settings, read once at startup:
/// <c>SMTP_HOST</c>, <c>SMTP_PORT</c> (587), <c>SMTP_USERNAME</c>,
/// <c>SMTP_PASSWORD</c>, <c>SMTP_FROM</c>, <c>SMTP_ENABLE_SSL</c> (true), and
/// <c>PUBLIC_APP_URL</c> (the frontend origin used in links, default
/// http://localhost:3000). With no <c>SMTP_HOST</c> nothing is sent.
/// </summary>
public sealed record EmailOptions(
    string? Host, int Port, string? Username, string? Password, string From, bool EnableSsl, string PublicAppUrl)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

    public static EmailOptions FromEnvironment(Func<string, string?> read)
    {
        static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var port = int.TryParse(Norm(read("SMTP_PORT")), out var p) && p is > 0 and < 65536 ? p : 587;
        var ssl = !string.Equals(Norm(read("SMTP_ENABLE_SSL")), "false", StringComparison.OrdinalIgnoreCase);
        var appUrl = Norm(read("PUBLIC_APP_URL"));
        if (appUrl is null || !Uri.TryCreate(appUrl, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            appUrl = "http://localhost:3000";
        return new EmailOptions(Norm(read("SMTP_HOST")), port, Norm(read("SMTP_USERNAME")), Norm(read("SMTP_PASSWORD")),
            Norm(read("SMTP_FROM")) ?? "BunonBrain <no-reply@localhost>", ssl, appUrl.TrimEnd('/'));
    }
}

/// <summary>Sends through the configured SMTP server.</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _opts;
    public SmtpEmailSender(EmailOptions opts) { _opts = opts; }

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient(_opts.Host!, _opts.Port)
        {
            EnableSsl = _opts.EnableSsl,
            Timeout = 15_000,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        if (_opts.Username is not null)
            client.Credentials = new NetworkCredential(_opts.Username, _opts.Password);
        using var mail = new MailMessage(MailAddressFrom(_opts.From), new MailAddress(message.To))
        {
            Subject = message.Subject,
            Body = message.TextBody,
            IsBodyHtml = false,
        };
        await client.SendMailAsync(mail, ct);
    }

    private static MailAddress MailAddressFrom(string from)
    {
        var lt = from.IndexOf('<');
        return lt > 0 && from.EndsWith('>')
            ? new MailAddress(from[(lt + 1)..^1].Trim(), from[..lt].Trim())
            : new MailAddress(from);
    }
}

/// <summary>
/// Used when no SMTP server is configured. Outside Production it writes the
/// message (including any link) to the log so the flow can be tried
/// locally; in Production it only warns that nothing was sent.
/// </summary>
public sealed class LogOnlyEmailSender : IEmailSender
{
    private readonly IHostEnvironment _env;
    private readonly ILogger<LogOnlyEmailSender> _log;
    public LogOnlyEmailSender(IHostEnvironment env, ILogger<LogOnlyEmailSender> log) { _env = env; _log = log; }

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (_env.IsProduction())
            _log.LogWarning("SMTP is not configured (SMTP_HOST); an email \"{Subject}\" was not sent.", message.Subject);
        else
            _log.LogWarning("SMTP is not configured, so this email was NOT sent. Local copy for testing:\nTo: {To}\nSubject: {Subject}\n\n{Body}",
                message.To, message.Subject, message.TextBody);
        return Task.CompletedTask;
    }
}

/// <summary>In-memory queue: requests return immediately and don't reveal, by timing, whether an email went out.</summary>
public sealed class EmailQueue : IEmailQueue
{
    private readonly Channel<EmailMessage> _channel = Channel.CreateBounded<EmailMessage>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly ILogger<EmailQueue> _log;
    public EmailQueue(ILogger<EmailQueue> log) { _log = log; }

    public ChannelReader<EmailMessage> Reader => _channel.Reader;

    public void Enqueue(EmailMessage message)
    {
        if (!_channel.Writer.TryWrite(message))
            _log.LogWarning("Email queue is full; an email \"{Subject}\" was dropped.", message.Subject);
    }
}

/// <summary>Sends queued emails in the background. A failure is logged by type only.</summary>
public sealed class EmailDispatcher : BackgroundService
{
    private readonly EmailQueue _queue;
    private readonly IEmailSender _sender;
    private readonly ILogger<EmailDispatcher> _log;

    public EmailDispatcher(EmailQueue queue, IEmailSender sender, ILogger<EmailDispatcher> log)
    { _queue = queue; _sender = sender; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await _sender.SendAsync(message, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Sending an email \"{Subject}\" failed ({ErrorType}).", message.Subject, ex.GetType().Name);
            }
        }
    }
}
