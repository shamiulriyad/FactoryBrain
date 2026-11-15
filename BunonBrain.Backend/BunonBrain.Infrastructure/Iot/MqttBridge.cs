using System.Buffers;
using System.Text;
using System.Threading.Channels;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Iot;
using BunonBrain.Application.Iot;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;

namespace BunonBrain.Infrastructure.Iot;

/// <summary>Live counters and connection state of the MQTT bridge (one per API instance).</summary>
public sealed class MqttBridgeState : IMqttBridgeState
{
    private readonly MqttOptions _o;
    private readonly object _gate = new();
    private long _received, _accepted, _duplicates, _rejected;
    private int _reconnects;
    private bool _connected;
    private DateTimeOffset? _connectedSince, _lastMessageAt, _lastErrorAt;
    private string? _lastError;

    public MqttBridgeState(IOptions<IotOptions> options) { _o = options.Value.Mqtt; }

    public void Connected() { lock (_gate) { _connected = true; _connectedSince = DateTimeOffset.UtcNow; } }
    public void Disconnected(string? reason)
    {
        lock (_gate)
        {
            if (_connected) _reconnects++;
            _connected = false;
            _connectedSince = null;
            if (reason is not null) { _lastError = reason; _lastErrorAt = DateTimeOffset.UtcNow; }
        }
    }
    public void Error(string message) { lock (_gate) { _lastError = message; _lastErrorAt = DateTimeOffset.UtcNow; } }
    public void Received() { Interlocked.Increment(ref _received); lock (_gate) _lastMessageAt = DateTimeOffset.UtcNow; }
    public void Outcome(string outcome)
    {
        switch (outcome)
        {
            case "Accepted": Interlocked.Increment(ref _accepted); break;
            case "Duplicate": Interlocked.Increment(ref _duplicates); break;
            default: Interlocked.Increment(ref _rejected); break;
        }
    }

    public IotBridgeStatusDto Snapshot()
    {
        lock (_gate)
        {
            // Never the credentials: host, port and whether TLS is used only.
            return new IotBridgeStatusDto(_o.Enabled, _connected, _o.Enabled ? $"{_o.Host}:{_o.Port}" : null, _o.UseTls, _o.ClientId, _o.TopicPrefix,
                IotTopics.Subscriptions(_o.TopicPrefix), _connectedSince, _lastMessageAt, Interlocked.Read(ref _received), Interlocked.Read(ref _accepted),
                Interlocked.Read(ref _duplicates), Interlocked.Read(ref _rejected), _lastError, _lastErrorAt, _reconnects,
                _o.Enabled
                    ? "The backend subscribes to the broker with its own account. Devices authenticate at the broker (one account per device, ACL limited to its own topics)."
                    : "MQTT is off (Iot__Mqtt__Enabled=false). HTTP device ingestion and the IoT simulator work without a broker.");
        }
    }
}

/// <summary>
/// The MQTT bridge: subscribes to the device topics, queues each message and
/// hands it, in arrival order, to the same IoT pipeline as HTTP and the
/// simulator. Reconnects with exponential backoff; keeps a persistent session
/// so QoS 1 messages published while the backend was down are delivered
/// afterwards; publishes a retained "online" status, with "offline" as its
/// last will. Credentials come from configuration only and are never logged.
/// </summary>
public sealed class MqttBridgeService : BackgroundService
{
    public const int QueueCapacity = 10_000;

    private readonly IServiceScopeFactory _scopes;
    private readonly MqttBridgeState _state;
    private readonly MqttOptions _o;
    private readonly ILogger<MqttBridgeService> _log;
    private readonly Channel<(string Topic, byte[] Payload)> _queue =
        Channel.CreateBounded<(string, byte[])>(new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    public MqttBridgeService(IServiceScopeFactory scopes, MqttBridgeState state, IOptions<IotOptions> options, ILogger<MqttBridgeService> log)
    {
        _scopes = scopes; _state = state; _o = options.Value.Mqtt; _log = log;
    }

    public static MqttClientOptions ClientOptions(MqttOptions o)
    {
        var b = new MqttClientFactory().CreateClientOptionsBuilder()
            .WithTcpServer(o.Host, o.Port)
            .WithClientId(o.ClientId)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(Math.Clamp(o.KeepAliveSeconds, 5, 600)))
            .WithCleanSession(!o.PersistentSession)
            .WithWillTopic(o.StatusTopic)
            .WithWillPayload(Encoding.UTF8.GetBytes("offline"))
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTimeout(TimeSpan.FromSeconds(10));
        if (!string.IsNullOrEmpty(o.Username)) b = b.WithCredentials(o.Username, o.Password);
        if (o.UseTls)
            b = b.WithTlsOptions(t =>
            {
                t.UseTls();
                if (o.AllowUntrustedCertificates) t.WithAllowUntrustedCertificates().WithIgnoreCertificateChainErrors().WithCertificateValidationHandler(_ => true);
            });
        return b.Build();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_o.Enabled)
        {
            _log.LogInformation("MQTT bridge disabled (Iot:Mqtt:Enabled=false).");
            return;
        }
        var consumer = Task.Run(() => ConsumeAsync(stoppingToken), stoppingToken);
        using var client = new MqttClientFactory().CreateMqttClient();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += async e =>
        {
            _state.Received();
            var payload = e.ApplicationMessage.Payload.ToArray();
            // Waits while the queue is full: back-pressure to the broker instead of dropping messages.
            await _queue.Writer.WriteAsync((e.ApplicationMessage.Topic, payload), stoppingToken);
        };
        client.DisconnectedAsync += e =>
        {
            _state.Disconnected(e.ClientWasConnected ? $"Disconnected: {e.Reason}" : null);
            disconnected.TrySetResult();
            return Task.CompletedTask;
        };

        var delay = TimeSpan.FromSeconds(Math.Max(1, _o.ReconnectMinSeconds));
        var options = ClientOptions(_o);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await client.ConnectAsync(options, stoppingToken);
                var subscribe = new MqttClientFactory().CreateSubscribeOptionsBuilder();
                foreach (var topic in IotTopics.Subscriptions(_o.TopicPrefix)) subscribe = subscribe.WithTopicFilter(f => f.WithTopic(topic).WithAtLeastOnceQoS());
                await client.SubscribeAsync(subscribe.Build(), stoppingToken);
                await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(_o.StatusTopic).WithPayload("online").WithRetainFlag()
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), stoppingToken);
                _state.Connected();
                _log.LogInformation("MQTT bridge connected to {Host}:{Port} (TLS {Tls}).", _o.Host, _o.Port, _o.UseTls);
                delay = TimeSpan.FromSeconds(Math.Max(1, _o.ReconnectMinSeconds));
                await disconnected.Task.WaitAsync(stoppingToken);
                _log.LogWarning("MQTT bridge disconnected; reconnecting.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Never log the options: they carry the password.
                _state.Error($"Connection failed: {ex.GetType().Name}: {ex.Message}");
                _log.LogWarning("MQTT bridge could not connect to {Host}:{Port}: {Error}. Retrying in {Delay}.", _o.Host, _o.Port, ex.Message, delay);
            }
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromSeconds(Math.Min(Math.Max(2, _o.ReconnectMaxSeconds), delay.TotalSeconds * 2));
        }
        try
        {
            if (client.IsConnected)
            {
                await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(_o.StatusTopic).WithPayload("offline").WithRetainFlag().Build(), CancellationToken.None);
                await client.DisconnectAsync();
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "MQTT bridge shutdown."); }
        _queue.Writer.TryComplete();
        try { await consumer; } catch (OperationCanceledException) { }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach (var (topic, payload) in _queue.Reader.ReadAllAsync(ct))
        {
            try { _state.Outcome(await DispatchAsync(_scopes, _o.TopicPrefix, topic, payload, ct)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _state.Outcome("Rejected");
                _state.Error($"Processing failed: {ex.GetType().Name}");
                _log.LogWarning(ex, "MQTT message on {Topic} could not be processed.", topic);
            }
        }
    }

    /// <summary>One MQTT message through the IoT pipeline. Returns Accepted, Duplicate or Rejected.</summary>
    public static async Task<string> DispatchAsync(IServiceScopeFactory scopes, string? prefix, string topic, byte[] payload, CancellationToken ct)
    {
        var parsed = IotTopics.Parse(topic, prefix);
        if (parsed is null) return "Rejected";
        await using var scope = scopes.CreateAsyncScope();
        var ingest = scope.ServiceProvider.GetRequiredService<IIotIngestionService>();
        var r = await ingest.IngestRawAsync(payload, new IotOrigin(IotTransports.Mqtt, null, null, null, parsed), ct);
        if (r.Outcome != AccessOutcome.Ok)
        {
            scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<MqttBridgeService>()
                .LogInformation("MQTT message on {Topic} refused: {Code} {Message}", topic, r.Code, r.Message);
            return "Rejected";
        }
        return r.Value!.Outcome;
    }
}
