using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Iot;
using BunonBrain.Application.Iot;
using BunonBrain.Infrastructure.Iot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace BunonBrain.Api.Tests;

/// <summary>
/// The MQTT bridge against a real, in-process MQTT broker (no Docker): it
/// connects with its own credentials, subscribes to the device topics, hands
/// every message in arrival order to the IoT pipeline, publishes its retained
/// online status, reconnects after the broker restarts, and refuses to leak
/// credentials when the broker rejects them. The pipeline is a recording fake
/// here; the database-backed pipeline is covered by DbIotTests.
/// </summary>
public sealed class MqttBridgeTests : IAsyncLifetime
{
    private const string BackendUser = "bunonbrain-backend";
    private const string BackendPassword = "test-only-password";
    private readonly int _port = FreePort();
    private MqttServer? _broker;
    private readonly RecordingIngestion _ingest = new();

    private sealed class RecordingIngestion : IIotIngestionService
    {
        public readonly ConcurrentQueue<(IotTopic Topic, string Payload, string Transport)> Messages = new();

        public Task<AccessResult<IotIngestResult>> IngestRawAsync(byte[] payload, IotOrigin origin, CancellationToken ct)
        {
            var json = Encoding.UTF8.GetString(payload);
            Messages.Enqueue((origin.Topic!, json, origin.Transport));
            var outcome = json.Contains("\"dup\"") ? "Duplicate" : "Accepted";
            return Task.FromResult(AccessResult<IotIngestResult>.Ok(new IotIngestResult(outcome, "TELEMETRY", Guid.NewGuid(), "S-01", false, null, null, null, null, null, null,
                Array.Empty<string>())));
        }

        public Task<AccessResult<IotIngestResult>> IngestAsync(IotMessage message, IotOrigin origin, CancellationToken ct) => throw new NotSupportedException();
        public Task<Guid?> AuthenticateDeviceAsync(Guid deviceId, string? key, CancellationToken ct) => Task.FromResult<Guid?>(null);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private async Task<MqttServer> StartBrokerAsync()
    {
        var factory = new MqttServerFactory();
        var server = factory.CreateMqttServer(factory.CreateServerOptionsBuilder()
            .WithDefaultEndpoint().WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointPort(_port).WithDefaultEndpointReuseAddress().Build());
        // Broker-side authentication: the backend and the test device have their own accounts.
        server.ValidatingConnectionAsync += e =>
        {
            var ok = (e.UserName == BackendUser && e.Password == BackendPassword) || (e.UserName == "device-s01" && e.Password == "device-pw");
            if (!ok) e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            return Task.CompletedTask;
        };
        await server.StartAsync();
        return server;
    }

    public async Task InitializeAsync() => _broker = await StartBrokerAsync();

    public async Task DisposeAsync()
    {
        if (_broker is not null) { await _broker.StopAsync(); _broker.Dispose(); }
    }

    private (MqttBridgeService Bridge, MqttBridgeState State) NewBridge(string password = BackendPassword, string prefix = "")
    {
        var options = Options.Create(new IotOptions
        {
            Mqtt = new MqttOptions
            {
                Enabled = true, Host = "127.0.0.1", Port = _port, Username = BackendUser, Password = password, ClientId = $"bridge-{Guid.NewGuid():N}",
                TopicPrefix = prefix, ReconnectMinSeconds = 1, ReconnectMaxSeconds = 2, KeepAliveSeconds = 5,
            },
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IIotIngestionService>(_ingest);
        var sp = services.BuildServiceProvider();
        var state = new MqttBridgeState(options);
        return (new MqttBridgeService(sp.GetRequiredService<IServiceScopeFactory>(), state, options, sp.GetRequiredService<ILogger<MqttBridgeService>>()), state);
    }

    private static async Task Until(Func<bool> condition, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(50);
        }
    }

    private async Task<IMqttClient> DeviceAsync()
    {
        var client = new MqttClientFactory().CreateMqttClient();
        await client.ConnectAsync(new MqttClientFactory().CreateClientOptionsBuilder().WithTcpServer("127.0.0.1", _port).WithClientId("S-01")
            .WithCredentials("device-s01", "device-pw").Build());
        return client;
    }

    private static Task PublishAsync(IMqttClient c, string topic, string json) =>
        c.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(json).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());

    [Fact]
    public async Task The_bridge_subscribes_with_its_own_account_and_hands_device_messages_to_the_pipeline_in_order()
    {
        var (bridge, state) = NewBridge();
        using var cts = new CancellationTokenSource();
        await bridge.StartAsync(cts.Token);
        try
        {
            await Until(() => state.Snapshot().Connected);
            using var device = await DeviceAsync();
            for (var i = 1; i <= 5; i++)
                await PublishAsync(device, "factory/MAIN/device/S-01/telemetry", JsonSerializer.Serialize(new { messageId = $"m-{i}", state = "RUNNING", powerWatts = 300 + i }));
            await PublishAsync(device, "factory/MAIN/machine/M-0101/production", """{"messageId":"c-1","deviceId":"PC-01","count":1}""");
            await PublishAsync(device, "factory/MAIN/device/S-01/status", """{"messageId":"dup","status":"ONLINE"}""");
            await PublishAsync(device, "elsewhere/MAIN/device/S-01/telemetry", """{"messageId":"x"}"""); // not a subscribed topic
            await Until(() => _ingest.Messages.Count >= 7);
            await Task.Delay(300);

            var all = _ingest.Messages.ToArray();
            Assert.Equal(7, all.Length);
            Assert.All(all, m => Assert.Equal(IotTransports.Mqtt, m.Transport));
            Assert.Equal(new[] { "m-1", "m-2", "m-3", "m-4", "m-5" },
                all.Take(5).Select(m => JsonDocument.Parse(m.Payload).RootElement.GetProperty("messageId").GetString()));
            Assert.Equal(("MAIN", IotTopic.DeviceSubject, "S-01", "telemetry"), (all[0].Topic.FactoryRef, all[0].Topic.Subject, all[0].Topic.SubjectRef, all[0].Topic.Channel));
            Assert.Equal((IotTopic.MachineSubject, "M-0101", "production"), (all[5].Topic.Subject, all[5].Topic.SubjectRef, all[5].Topic.Channel));
            Assert.Equal("status", all[6].Topic.Channel);

            var snap = state.Snapshot();
            Assert.Equal(7, snap.Received);
            Assert.Equal(6, snap.Accepted);
            Assert.Equal(1, snap.Duplicates);
            Assert.Equal($"127.0.0.1:{_port}", snap.Broker);
            Assert.Equal(4, snap.Subscriptions.Count);

            // The backend's retained status tells devices and dashboards it is online.
            var retained = await _broker!.GetRetainedMessagesAsync();
            var status = retained.Single(r => r.Topic == "bunonbrain/backend/status");
            Assert.Equal("online", Encoding.UTF8.GetString(status.Payload));
        }
        finally
        {
            await cts.CancelAsync();
            await bridge.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_bridge_reconnects_and_resubscribes_after_the_broker_restarts()
    {
        var (bridge, state) = NewBridge(prefix: "bunonbrain");
        using var cts = new CancellationTokenSource();
        await bridge.StartAsync(cts.Token);
        try
        {
            await Until(() => state.Snapshot().Connected);
            var broker = _broker!;
            await broker.StopAsync();
            broker.Dispose();
            await Until(() => !state.Snapshot().Connected);
            _broker = await StartBrokerAsync();
            await Until(() => state.Snapshot().Connected, 30);
            Assert.True(state.Snapshot().Reconnects >= 1);

            using var device = await DeviceAsync();
            await PublishAsync(device, "bunonbrain/factory/MAIN/device/S-01/heartbeat", """{"messageId":"h-after-restart"}""");
            await Until(() => _ingest.Messages.Any(m => m.Payload.Contains("h-after-restart")));
            Assert.Equal("heartbeat", _ingest.Messages.Last().Topic.Channel);
        }
        finally
        {
            await cts.CancelAsync();
            await bridge.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Rejected_credentials_are_reported_without_revealing_the_password()
    {
        var (bridge, state) = NewBridge(password: "wrong-secret-value");
        using var cts = new CancellationTokenSource();
        await bridge.StartAsync(cts.Token);
        try
        {
            await Until(() => state.Snapshot().LastError is not null);
            var snap = state.Snapshot();
            Assert.False(snap.Connected);
            Assert.DoesNotContain("wrong-secret-value", JsonSerializer.Serialize(snap));
            Assert.DoesNotContain(BackendUser, JsonSerializer.Serialize(snap));
            Assert.Empty(_ingest.Messages);
        }
        finally
        {
            await cts.CancelAsync();
            await bridge.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Client_options_carry_the_last_will_and_never_hardcoded_credentials()
    {
        var o = new MqttOptions { Host = "broker.local", Port = 8883, UseTls = true, Username = "svc", Password = "from-env" };
        var options = MqttBridgeService.ClientOptions(o);
        Assert.Equal(o.StatusTopic, options.WillTopic);
        Assert.True(options.WillRetain);
        Assert.Equal("offline", Encoding.UTF8.GetString(options.WillPayload));
        Assert.False(options.CleanSession); // persistent session: QoS 1 messages wait for the backend
        Assert.Equal("svc", options.Credentials!.GetUserName(options));
        var none = MqttBridgeService.ClientOptions(new MqttOptions());
        Assert.Null(none.Credentials);
    }

    [Fact]
    public async Task Messages_on_foreign_topics_never_reach_the_pipeline()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IIotIngestionService>(_ingest);
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        Assert.Equal("Rejected", await MqttBridgeService.DispatchAsync(scopes, null, "factory/MAIN/device/S-01/firmware", Encoding.UTF8.GetBytes("{}"), default));
        Assert.Equal("Rejected", await MqttBridgeService.DispatchAsync(scopes, "bb", "factory/MAIN/device/S-01/telemetry", Encoding.UTF8.GetBytes("{}"), default));
        Assert.Empty(_ingest.Messages);
        Assert.Equal("Accepted", await MqttBridgeService.DispatchAsync(scopes, "bb", "bb/factory/MAIN/device/S-01/telemetry", Encoding.UTF8.GetBytes("""{"messageId":"m"}"""), default));
        Assert.Single(_ingest.Messages);
    }
}
