using BunonBrain.Api.Authorization;
using BunonBrain.Application.Access;
using BunonBrain.Application.Monitoring;
using BunonBrain.Infrastructure.Monitoring;

namespace BunonBrain.Api.Tests;

/// <summary>The computer-vision service's sign-in settings, reach and event rules, without a database.</summary>
public sealed class CvServiceUnitTests
{
    private static Func<string, string?> Env(string? key, string? factories) =>
        n => n == ServiceKeyDefaults.KeyEnv ? key : n == ServiceKeyDefaults.FactoriesEnv ? factories : null;

    [Fact]
    public void A_service_key_must_be_long_enough_and_is_kept_only_as_a_hash()
    {
        Assert.False(ServiceKeySettings.FromEnvironment(Env(null, "MAIN")).Enabled);
        var shortKey = ServiceKeySettings.FromEnvironment(Env("too-short", "MAIN"));
        Assert.False(shortKey.Enabled);
        Assert.Contains("shorter than 32", shortKey.Problem);

        var key = new string('k', 40);
        var s = ServiceKeySettings.FromEnvironment(Env(key, "main, dhk-1"));
        Assert.True(s.Enabled);
        Assert.Equal(new[] { "MAIN", "DHK-1" }, s.FactoryCodes);
        Assert.False(s.AllFactories);
        Assert.True(s.Matches(key));
        Assert.True(s.Matches($" {key} "));
        Assert.False(s.Matches(key + "x"));
        Assert.False(s.Matches(""));
        Assert.DoesNotContain(key, Convert.ToBase64String(s.KeyHash!));

        Assert.True(ServiceKeySettings.FromEnvironment(Env(key, "*")).AllFactories);
        Assert.Contains("reaches no factory", ServiceKeySettings.FromEnvironment(Env(key, "")).Problem);
    }

    [Fact]
    public void A_service_principal_has_no_role_and_reaches_only_its_factories()
    {
        var f = Guid.NewGuid();
        var svc = new AccessContext(Guid.NewGuid(), "service:computer-vision", Array.Empty<string>(), ServiceKeyDefaults.ComputerVisionPermissions,
            new[] { f }, Array.Empty<string>(), serviceName: "computer-vision");
        Assert.True(svc.IsService);
        Assert.Equal(RoleScope.Factory, svc.Scope);
        Assert.True(svc.CanAccessFactory(f));
        Assert.False(svc.CanAccessFactory(Guid.NewGuid()));
        Assert.Equal("Service", svc.PrimaryRole);
        Assert.Equal(new[] { Permissions.MonitoringEventsIngest }, svc.Permissions.ToArray());
        Assert.False(svc.Has(Permissions.MonitoringView));

        var all = new AccessContext(Guid.NewGuid(), "svc", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<Guid>(), Array.Empty<string>(),
            serviceName: "computer-vision", serviceAllFactories: true);
        Assert.Equal(RoleScope.Platform, all.Scope);
        // A person's context is unchanged by the new parameters.
        Assert.False(new AccessContext(Guid.NewGuid(), "p", new[] { SystemRoles.Worker }, Array.Empty<string>(), Array.Empty<Guid>(), Array.Empty<string>()).IsService);
    }

    [Fact]
    public void Events_from_the_cv_service_are_external_and_limited_to_vision_camera_and_model_types()
    {
        Assert.True(MonitoringProducers.IsExternal(MonitoringProducers.CvService));
        Assert.True(MonitoringProducers.IsExternal(MonitoringProducers.Api));
        Assert.False(MonitoringProducers.IsExternal(MonitoringProducers.Simulator));
        Assert.Contains(MonitoringEventTypes.ZoneEntered, MonitoringProducers.CvEventTypes);
        Assert.Contains(MonitoringEventTypes.ModelStatusChanged, MonitoringProducers.CvEventTypes);
        Assert.DoesNotContain(MonitoringEventTypes.DeviceStatusChanged, MonitoringProducers.CvEventTypes);
        // Activity recognition reports predicted posture / movement of its own temporary tracks.
        foreach (var t in MonitoringEventTypes.Activity) Assert.Contains(t, MonitoringProducers.CvEventTypes);
    }

    [Theory]
    [InlineData("rtsp://10.20.30.40:554/Streaming/101", "cam", "p@ss:w/rd", "rtsp://cam:p%40ss%3Aw%2Frd@10.20.30.40:554/Streaming/101")]
    [InlineData("rtsp://10.20.30.40/live?channel=1", "admin", "x", "rtsp://admin:x@10.20.30.40/live?channel=1")]
    [InlineData("rtsp://10.20.30.40/live", null, null, "rtsp://10.20.30.40/live")]
    public void Stream_credentials_are_put_back_into_the_url_escaped(string url, string? user, string? password, string expected) =>
        Assert.Equal(expected, CvIntegrationService.WithCredentials(url, user, password));
}
