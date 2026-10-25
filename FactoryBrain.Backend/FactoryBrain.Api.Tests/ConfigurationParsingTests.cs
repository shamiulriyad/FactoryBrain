using FactoryBrain.Api.Middleware;
using FactoryBrain.Application.Settings;
using FactoryBrain.Infrastructure.Services;

namespace FactoryBrain.Api.Tests;

/// <summary>
/// Parsing of the optional LLM_BASE_URL and LOGIN_RATE_LIMIT_PER_MINUTE
/// settings. LOGIN_RATE_LIMIT_PER_MINUTE reads a process variable, so these
/// run in the shared (non-parallel) collection and restore it afterwards.
/// LLM_BASE_URL is part of the startup configuration, read once.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ConfigurationParsingTests
{
    private static T With<T>(string name, string? value, Func<T> read)
    {
        var before = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try { return read(); }
        finally { Environment.SetEnvironmentVariable(name, before); }
    }

    [Theory]
    [InlineData(null, "https://api.openai.com")]
    [InlineData("", "https://api.openai.com")]
    [InlineData("http://127.0.0.1:4010", "http://127.0.0.1:4010")]
    [InlineData("https://llm.internal.example/v1/ignored?x=1", "https://llm.internal.example")]
    [InlineData("ftp://llm.example", "https://api.openai.com")]
    [InlineData("file:///etc/passwd", "https://api.openai.com")]
    [InlineData("not a url", "https://api.openai.com")]
    public void Llm_base_url_accepts_only_absolute_http_urls(string? value, string expected)
    {
        var startup = StartupAiConfig.FromEnvironment(name => name == "LLM_BASE_URL" ? value : null);
        Assert.Equal(expected, AskService.OpenAiBaseUrl(startup.LlmBaseUrl));
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData("60", 60)]
    [InlineData("1", 1)]
    [InlineData("1000", 1000)]
    [InlineData("0", 5)]
    [InlineData("1001", 5)]
    [InlineData("-3", 5)]
    [InlineData("lots", 5)]
    public void Login_rate_limit_override_is_bounded(string? value, int expected)
    {
        Assert.Equal(expected, With("LOGIN_RATE_LIMIT_PER_MINUTE", value, RateLimitPolicies.LoginPerMinute));
    }

    [Theory]
    [InlineData(null, false, false)]   // default: off outside Development
    [InlineData(null, true, true)]     // default: on in Development
    [InlineData("true", false, true)]
    [InlineData("TRUE", false, true)]
    [InlineData("false", true, false)] // can be switched off in Development too
    [InlineData("yes", false, false)]  // anything else keeps the default
    public void Swagger_is_development_only_unless_explicitly_switched(string? value, bool isDevelopment, bool expected)
    {
        Assert.Equal(expected, Program.SwaggerEnabled(value, isDevelopment));
    }
}

/// <summary>The test host is not Development and doesn't set SWAGGER_ENABLED, so no API description is served.</summary>
[Collection(ApiCollection.Name)]
public sealed class SwaggerDefaultTests
{
    private readonly ApiFactory _factory;
    public SwaggerDefaultTests(ApiFactory factory) { _factory = factory; factory.Activate(); }

    [Fact]
    public async Task Swagger_is_not_served_by_default_outside_development()
    {
        using var client = _factory.CreateClient();
        using var doc = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.NotEqual(System.Net.HttpStatusCode.OK, doc.StatusCode);
        Assert.DoesNotContain("\"openapi\"", await doc.Content.ReadAsStringAsync());
    }
}
