using System.Security.Cryptography;
using System.Text;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Cameras;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Dtos.Cameras;
using BunonBrain.Application.Dtos.Devices;
using BunonBrain.Application.Validation;
using BunonBrain.Infrastructure.Devices;
using BunonBrain.Infrastructure.Settings;
using BunonBrain.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using P = BunonBrain.Application.Access.Permissions;
using R = BunonBrain.Application.Access.SystemRoles;

namespace BunonBrain.Api.Tests;

/// <summary>Camera and device rules that need no database: zone geometry, stream URLs, video detection, health, credentials and role permissions.</summary>
public sealed class CameraDeviceUnitTests
{
    private static ZonePoint[] Pts(params double[] xy) => Enumerable.Range(0, xy.Length / 2).Select(i => new ZonePoint(xy[2 * i], xy[2 * i + 1])).ToArray();

    // ─── Zone geometry ─────────────────────────────────────────────────────

    [Fact]
    public void A_simple_polygon_is_accepted_rounded_and_a_closing_point_dropped()
    {
        var clean = ZoneGeometry.Normalise(Pts(0.1, 0.1, 0.4, 0.1, 0.4, 0.5, 0.1234567891, 0.5, 0.1, 0.1), out var error, out _);
        Assert.Null(error);
        Assert.NotNull(clean);
        Assert.Equal(4, clean!.Count);
        Assert.Equal(0.123457, clean[3].X);
        Assert.InRange(Math.Abs(ZoneGeometry.SignedArea(clean)), 0.1, 0.13);
        Assert.Equal(clean, ZoneGeometry.Deserialize(ZoneGeometry.Serialize(clean)));
    }

    public static IEnumerable<object[]> BadPolygons => new[]
    {
        new object[] { Pts(0.1, 0.1, 0.5, 0.5), ZoneGeometry.ErrCount },
        new object[] { Pts(0.1, 0.1, 1.2, 0.1, 0.5, 0.5), ZoneGeometry.ErrRange },
        new object[] { Pts(-0.01, 0.1, 0.9, 0.1, 0.5, 0.5), ZoneGeometry.ErrRange },
        new object[] { Pts(0.1, 0.1, double.NaN, 0.1, 0.5, 0.5), ZoneGeometry.ErrRange },
        new object[] { Pts(0.1, 0.1, 0.1, 0.1, 0.5, 0.5, 0.2, 0.6), ZoneGeometry.ErrDuplicate },
        new object[] { Pts(0.1, 0.1, 0.2, 0.2, 0.3, 0.3), ZoneGeometry.ErrArea },           // collinear
        new object[] { Pts(0.1, 0.1, 0.105, 0.1, 0.105, 0.105), ZoneGeometry.ErrArea },     // a speck
        new object[] { Pts(0.1, 0.1, 0.5, 0.5, 0.5, 0.1, 0.1, 0.5), ZoneGeometry.ErrSelfIntersecting }, // bow tie
    };

    [Theory]
    [MemberData(nameof(BadPolygons))]
    public void Invalid_polygons_are_refused_with_a_reason(ZonePoint[] points, string expected)
    {
        Assert.Null(ZoneGeometry.Normalise(points, out var error, out var message));
        Assert.Equal(expected, error);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public void Too_many_points_and_null_input_are_refused()
    {
        var circle = Enumerable.Range(0, ZoneGeometry.MaxPoints + 1)
            .Select(i => new ZonePoint(0.5 + 0.4 * Math.Cos(i * 2 * Math.PI / 65), 0.5 + 0.4 * Math.Sin(i * 2 * Math.PI / 65))).ToArray();
        Assert.Null(ZoneGeometry.Normalise(circle, out var e1, out _));
        Assert.Equal(ZoneGeometry.ErrCount, e1);
        Assert.Null(ZoneGeometry.Normalise(null, out var e2, out _));
        Assert.Equal(ZoneGeometry.ErrCount, e2);
        Assert.NotNull(ZoneGeometry.Normalise(circle[..64], out _, out _));
    }

    // ─── Stream URLs ───────────────────────────────────────────────────────

    [Fact]
    public void Credentials_in_an_rtsp_url_are_split_off_and_the_stored_url_has_none()
    {
        Assert.True(StreamUrls.TryParse("rtsp://admin:S3cr%40t!@10.0.4.21:554/Streaming/Channels/101", CameraSourceTypes.Rtsp, out var p, out _, out _));
        Assert.Equal("rtsp://10.0.4.21:554/Streaming/Channels/101", p!.Url);
        Assert.Equal("10.0.4.21", p.Host);
        Assert.Equal("admin", p.UserName);
        Assert.Equal("S3cr@t!", p.Password);
        Assert.DoesNotContain("admin", p.Url);
        Assert.DoesNotContain("S3cr", p.Url);

        Assert.True(StreamUrls.TryParse("rtsps://cam-07.factory.lan/live?channel=2", CameraSourceTypes.Rtsp, out var q, out _, out _));
        Assert.Null(q!.UserName);
        Assert.Equal("rtsps://cam-07.factory.lan/live?channel=2", q.Url);
    }

    [Theory]
    [InlineData("http://10.0.4.21/stream", "Rtsp", StreamUrls.ErrScheme)]
    [InlineData("rtsp://10.0.4.21/stream", "HttpStream", StreamUrls.ErrScheme)]
    [InlineData("file:///etc/passwd", "Rtsp", StreamUrls.ErrScheme)]
    [InlineData("rtsp://127.0.0.1:554/live", "Rtsp", StreamUrls.ErrHost)]
    [InlineData("rtsp://localhost/live", "Rtsp", StreamUrls.ErrHost)]
    [InlineData("http://169.254.169.254/latest/meta-data", "HttpStream", StreamUrls.ErrHost)]
    [InlineData("rtsp://0.0.0.0/live", "Rtsp", StreamUrls.ErrHost)]
    [InlineData("rtsp://[::1]/live", "Rtsp", StreamUrls.ErrHost)]
    [InlineData("rtsp://239.1.1.1/live", "Rtsp", StreamUrls.ErrHost)]
    [InlineData("rtsp://10.0.4.21/live?user=admin&password=x", "Rtsp", StreamUrls.ErrSecretInQuery)]
    [InlineData("http://10.0.4.21/mjpeg?token=abc", "HttpStream", StreamUrls.ErrSecretInQuery)]
    [InlineData("rtsp://10.0.4.21/live#frag", "Rtsp", StreamUrls.ErrInvalid)]
    [InlineData("rtsp://10.0.4.21/li ve", "Rtsp", StreamUrls.ErrInvalid)]
    [InlineData("not a url", "Rtsp", StreamUrls.ErrInvalid)]
    public void Unsafe_or_malformed_stream_urls_are_refused(string url, string type, string expected)
    {
        Assert.False(StreamUrls.TryParse(url, type, out _, out var error, out var message));
        Assert.Equal(expected, error);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public void Camera_credentials_are_bound_to_their_source_row()
    {
        var protector = new AiSecretProtector(RandomNumberGenerator.GetBytes(32));
        var a = Guid.NewGuid();
        var json = StreamUrls.CredentialsJson("admin", "S3cret!");
        var blob = protector.Protect(json, CameraRules.CredentialsPurposeFor(a));
        Assert.DoesNotContain("S3cret!", Encoding.UTF8.GetString(blob));
        Assert.True(protector.TryUnprotect(blob, CameraRules.CredentialsPurposeFor(a), out var back));
        Assert.Equal(json, back);
        // Copied onto another camera's source row, it no longer decrypts.
        Assert.False(protector.TryUnprotect(blob, CameraRules.CredentialsPurposeFor(Guid.NewGuid()), out _));
        // Nor under the AI settings purposes.
        Assert.False(protector.TryUnprotect(blob, AiSecretProtector.LlmApiKeyPurpose, out _));
    }

    // ─── Video files ───────────────────────────────────────────────────────

    [Fact]
    public void Videos_are_identified_by_their_bytes()
    {
        var mp4 = new byte[] { 0, 0, 0, 0x20 }.Concat("ftypisom"u8.ToArray()).Concat(new byte[8]).ToArray();
        Assert.True(VideoFileInspector.TryInspect(mp4, out var info, out _));
        Assert.Equal("video/mp4", info!.ContentType);
        var webm = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81 };
        Assert.True(VideoFileInspector.TryInspect(webm, out info, out _));
        Assert.Equal("webm", info!.Extension);
        var mov = new byte[] { 0, 0, 0, 0x14 }.Concat("ftypqt  "u8.ToArray()).ToArray();
        Assert.False(VideoFileInspector.TryInspect(mov, out _, out var e1));
        Assert.Equal(VideoFileInspector.ErrUnsupported, e1);
        Assert.False(VideoFileInspector.TryInspect(TestImages.Png(4, 4), out _, out var e2));
        Assert.Equal(VideoFileInspector.ErrUnsupported, e2);
        Assert.False(VideoFileInspector.TryInspect(ReadOnlySpan<byte>.Empty, out _, out var e3));
        Assert.Equal(VideoFileInspector.ErrEmpty, e3);
    }

    [Fact]
    public async Task The_streaming_file_store_stops_at_the_limit_and_leaves_nothing_behind()
    {
        var root = Path.Combine(Path.GetTempPath(), "bunonbrain-camera-unit", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SafeFileStore(root, new[] { "mp4" }, NullLogger.Instance, "camera video");
            var key = await store.SaveAsync(new MemoryStream(new byte[1000]), "mp4", 1000, default);
            Assert.True(store.IsValidKey(key));
            await Assert.ThrowsAsync<FileTooLargeException>(() => store.SaveAsync(new MemoryStream(new byte[1001]), "mp4", 1000, default));
            Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // ─── Health ────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_real_device_with_a_recent_heartbeat_is_online_and_physically_connected()
    {
        var h = DeviceRules.Health(DeviceStatuses.Active, ConnectionStatuses.Online, false, Now.AddSeconds(-30), 180, Now);
        Assert.Equal(HealthStatuses.Online, h.Health);
        Assert.True(h.PhysicallyConnected);
        Assert.False(h.Stale);
        Assert.Equal("Online", h.Label);
    }

    [Fact]
    public void A_simulated_device_is_never_physically_connected_and_always_labelled()
    {
        var h = DeviceRules.Health(DeviceStatuses.Active, ConnectionStatuses.Online, true, Now.AddSeconds(-30), 180, Now);
        Assert.Equal(HealthStatuses.Online, h.Health);
        Assert.False(h.PhysicallyConnected);
        Assert.Contains("simulated", h.Label);
        Assert.Contains("simulated", DeviceRules.Health(DeviceStatuses.Active, ConnectionStatuses.Offline, true, Now, 180, Now).Label);
        Assert.Contains("simulated", DeviceRules.Health(DeviceStatuses.Active, ConnectionStatuses.Unknown, true, null, 180, Now).Label);
    }

    [Fact]
    public void Stopped_heartbeats_count_as_offline_before_the_monitor_runs()
    {
        var h = DeviceRules.Health(DeviceStatuses.Active, ConnectionStatuses.Online, false, Now.AddSeconds(-181), 180, Now);
        Assert.Equal(HealthStatuses.Offline, h.Health);
        Assert.Equal(ConnectionStatuses.Offline, h.Connection);
        Assert.True(h.Stale);
        Assert.False(h.PhysicallyConnected);
        Assert.Equal(HealthStatuses.Online, DeviceRules.Health(DeviceStatuses.Active, ConnectionStatuses.Online, false, Now.AddSeconds(-180), 180, Now).Health);
    }

    [Theory]
    [InlineData("Disabled", "Online", "Disabled")]
    [InlineData("Maintenance", "Online", "Maintenance")]
    [InlineData("Maintenance", "Offline", "Maintenance")]
    [InlineData("Active", "Unknown", "Unknown")]
    [InlineData("Active", "Offline", "Offline")]
    public void Administrative_status_comes_before_connection(string status, string connection, string expected) =>
        Assert.Equal(expected, DeviceRules.Health(status, connection, false, Now, 180, Now).Health);

    [Fact]
    public void Disabled_devices_are_never_physically_connected() =>
        Assert.False(DeviceRules.Health(DeviceStatuses.Disabled, ConnectionStatuses.Online, false, Now, 180, Now).PhysicallyConnected);

    [Fact]
    public void Metadata_is_bounded_and_refuses_secrets()
    {
        Assert.True(DeviceRules.TryMetadata(new Dictionary<string, string?> { ["mac"] = " AA:BB:CC:DD:EE:FF ", ["ip"] = "10.0.0.5" }, out var json, out _));
        Assert.Equal("{\"ip\":\"10.0.0.5\",\"mac\":\"AA:BB:CC:DD:EE:FF\"}", json);
        Assert.False(DeviceRules.TryMetadata(new Dictionary<string, string?> { ["wifi_password"] = "x" }, out _, out var e1));
        Assert.Contains("secret", e1);
        Assert.False(DeviceRules.TryMetadata(new Dictionary<string, string?> { ["bad key!"] = "x" }, out _, out _));
        Assert.False(DeviceRules.TryMetadata(Enumerable.Range(0, 31).ToDictionary(i => $"k{i}", i => (string?)"v"), out _, out _));
        Assert.False(DeviceRules.TryMetrics(new Dictionary<string, double> { ["rssi"] = double.PositiveInfinity }, out _, out _));
        Assert.True(DeviceRules.TryMetrics(null, out var none, out _));
        Assert.Null(none);
    }

    [Fact]
    public void Every_category_has_a_sample_error()
    {
        foreach (var c in DeviceCategories.All)
        {
            var (code, message) = DeviceIngestionService.SampleError(c);
            Assert.False(string.IsNullOrWhiteSpace(code));
            Assert.StartsWith("Sample error", message);
        }
    }

    // ─── Codes & validation ────────────────────────────────────────────────

    [Fact]
    public void Camera_codes_continue_the_cam_series()
    {
        Assert.Equal("CAM-001", CameraRules.NextCode(Array.Empty<string>()));
        Assert.Equal("CAM-013", CameraRules.NextCode(new[] { "CAM-002", "CAM-012", "GATE-9", "CAM-X" }));
        Assert.True(CameraRules.IsCode("cam-001"));
        Assert.False(CameraRules.IsCode("-CAM"));
        Assert.True(DeviceRules.IsCode("RFID-GATE:1"));
    }

    [Fact]
    public void Requests_are_validated_before_any_service_runs()
    {
        Assert.False(new SetCameraStatusRequestValidator().Validate(new SetCameraStatusRequest("Inactive")).IsValid); // reason required
        Assert.True(new SetCameraStatusRequestValidator().Validate(new SetCameraStatusRequest("Active")).IsValid);
        Assert.False(new CreateCameraRequestValidator().Validate(new CreateCameraRequest(Guid.NewGuid(), "Cam", "Thermal", Guid.NewGuid())).IsValid);
        Assert.False(new CreateCameraRequestValidator().Validate(new CreateCameraRequest(Guid.NewGuid(), "Cam", "IpCamera", Guid.Empty)).IsValid);
        Assert.True(new CreateCameraRequestValidator().Validate(new CreateCameraRequest(Guid.NewGuid(), "Cam", "ipcamera", Guid.NewGuid())).IsValid);
        Assert.False(new SetCameraSourceRequestValidator().Validate(new SetCameraSourceRequest("Rtsp")).IsValid); // URL required
        Assert.False(new CreateCameraZoneRequestValidator().Validate(new CreateCameraZoneRequest("Z", "Workstation", Pts(0, 0, 1, 0, 1, 1), Color: "red")).IsValid);
        Assert.False(new UpdateDeviceRequestValidator().Validate(new UpdateDeviceRequest("D", "Camera", null, null, null, null, null, null, null, null, null, null, null, 60, 60)).IsValid);
        Assert.False(new DeviceReportRequestValidator().Validate(new DeviceReportRequest("Heartbeat")).IsValid);
        Assert.False(new DeviceReportRequestValidator().Validate(new DeviceReportRequest("Error")).IsValid); // code or message
        Assert.True(new DeviceReportRequestValidator().Validate(new DeviceReportRequest("Error", Code: "E1")).IsValid);
        Assert.False(new SimulatorHeartbeatRequestValidator().Validate(new SimulatorHeartbeatRequest(Guid.NewGuid(), Count: 21)).IsValid);
    }

    // ─── Roles ─────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> PermsOf(string role) => AccessCatalog.Role(role)!.Permissions;

    [Fact]
    public void Camera_and_device_permissions_follow_the_role_matrix()
    {
        foreach (var p in new[] { P.CamerasView, P.CamerasManage, P.DevicesView, P.DevicesManage, P.DevicesLogsView, P.DevicesSimulate })
        {
            Assert.False(AccessCatalog.Permission(p)!.Reserved, $"{p} is used by endpoints and can't be reserved");
            Assert.Contains(p, PermsOf(R.SuperAdmin));
            Assert.Contains(p, PermsOf(R.FactoryAdmin));
            Assert.Contains(p, PermsOf(R.DeviceAdmin));
        }
        Assert.Contains(P.CamerasView, PermsOf(R.FactoryManager));
        Assert.Contains(P.DevicesView, PermsOf(R.FactoryManager));
        Assert.DoesNotContain(P.CamerasManage, PermsOf(R.FactoryManager));
        Assert.DoesNotContain(P.DevicesManage, PermsOf(R.FactoryManager));
        Assert.Contains(P.CamerasView, PermsOf(R.ProductionSupervisor));
        Assert.DoesNotContain(P.CamerasManage, PermsOf(R.ProductionSupervisor));
        Assert.Contains(P.CamerasView, PermsOf(R.MonitoringOperator));
        Assert.DoesNotContain(P.CamerasManage, PermsOf(R.MonitoringOperator));
        Assert.DoesNotContain(P.DevicesSimulate, PermsOf(R.MonitoringOperator));
        foreach (var role in new[] { R.HrOfficer, R.Worker })
            foreach (var p in new[] { P.CamerasView, P.CamerasManage, P.DevicesManage, P.DevicesSimulate, P.DevicesLogsView })
                Assert.DoesNotContain(p, PermsOf(role));
        // A device administrator still can't touch HR data.
        foreach (var p in new[] { P.WorkersManage, P.AttendanceManage, P.WorkersView })
            Assert.DoesNotContain(p, PermsOf(R.DeviceAdmin));
    }

    [Fact]
    public void Supervisors_reach_only_cameras_on_their_lines_and_never_configure()
    {
        var f = Guid.NewGuid();
        var supervisor = new AccessContext(Guid.NewGuid(), "s@x", new[] { R.ProductionSupervisor }, PermsOf(R.ProductionSupervisor), new[] { f }, new[] { "line-1" });
        Assert.True(supervisor.IsLineScoped);
        Assert.False(supervisor.Has(P.CamerasManage));
        var deviceAdmin = new AccessContext(Guid.NewGuid(), "d@x", new[] { R.DeviceAdmin }, PermsOf(R.DeviceAdmin), new[] { f }, Array.Empty<string>());
        Assert.True(DeviceQueries.CanConfigure(deviceAdmin, f));
        Assert.False(DeviceQueries.CanConfigure(deviceAdmin, Guid.NewGuid()));
        Assert.False(DeviceQueries.CanConfigure(supervisor, f));
    }
}
