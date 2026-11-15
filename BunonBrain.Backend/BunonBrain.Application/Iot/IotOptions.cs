namespace BunonBrain.Application.Iot;

/// <summary>
/// IoT settings (section <c>Iot</c>; environment <c>Iot__…</c>). Broker
/// credentials only come from configuration (environment / secret store),
/// never from code or the database, and are never returned by any API.
/// </summary>
public sealed class IotOptions
{
    private int _retention = 30;
    private int _perSecond = 20;
    private int _perMinute = 600;
    private int _monitor = 30;

    /// <summary>Raw telemetry older than this is deleted (state history, counter events and corrections are kept).</summary>
    public int TelemetryRetentionDays { get => _retention; set => _retention = Math.Clamp(value, 1, 3650); }

    public int MessagesPerSecondPerDevice { get => _perSecond; set => _perSecond = Math.Clamp(value, 1, 1000); }
    public int MessagesPerMinutePerDevice { get => _perMinute; set => _perMinute = Math.Clamp(value, 1, 60_000); }

    /// <summary>How often the machine monitor checks health, silence and timed alert rules.</summary>
    public int MonitorIntervalSeconds { get => _monitor; set => _monitor = Math.Clamp(value, 5, 600); }

    public MqttOptions Mqtt { get; set; } = new();
}

public sealed class MqttOptions
{
    /// <summary>Off by default: HTTP ingestion and the simulator work without a broker.</summary>
    public bool Enabled { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    /// <summary>TLS to the broker (port 8883 usually). Use it whenever the broker is not on the same host.</summary>
    public bool UseTls { get; set; }
    /// <summary>Development only: accept a self-signed broker certificate.</summary>
    public bool AllowUntrustedCertificates { get; set; }
    public string ClientId { get; set; } = "bunonbrain-backend";
    /// <summary>The backend's broker account (env <c>Iot__Mqtt__Username</c>).</summary>
    public string? Username { get; set; }
    /// <summary>Env <c>Iot__Mqtt__Password</c>. Never logged or returned.</summary>
    public string? Password { get; set; }
    /// <summary>Optional first topic level(s), e.g. <c>bunonbrain</c> ⇒ <c>bunonbrain/factory/…</c>.</summary>
    public string TopicPrefix { get; set; } = "";
    public int KeepAliveSeconds { get; set; } = 30;
    public int ReconnectMinSeconds { get; set; } = 2;
    public int ReconnectMaxSeconds { get; set; } = 60;
    /// <summary>Keep the subscription session while the backend is down, so QoS 1 messages are delivered after a restart.</summary>
    public bool PersistentSession { get; set; } = true;
    /// <summary>Retained "online" here while connected; the broker publishes "offline" (last will) if the backend drops.</summary>
    public string StatusTopic { get; set; } = "bunonbrain/backend/status";
}
