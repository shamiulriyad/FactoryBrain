using FactoryBrain.Application.Dtos.Settings;
using FactoryBrain.Application.Abstractions.Interfaces;

namespace FactoryBrain.Api.Services;

/// <summary>
/// Trim-only mirror of <c>src/app/api/settings/llm/route.ts</c>. Never
/// echoes the API key. Updates <c>process.env</c> + (dev only)
/// <c>.env.local</c> via DotNetEnv's in-memory load.
/// </summary>
public sealed class LlmSettingsService : ILlmSettingsService
{
    private static readonly HashSet<string> _supported = new() { "openai", "anthropic", "gemini" };
    private readonly IWebHostEnvironment _env;

    public LlmSettingsService(IWebHostEnvironment env) { _env = env; }

    public LlmSettingsResponse ReadStatus()
    {
        var provider = Norm(Environment.GetEnvironmentVariable("LLM_PROVIDER"));
        var model    = Norm(Environment.GetEnvironmentVariable("LLM_MODEL"));
        var apiKey   = Norm(Environment.GetEnvironmentVariable("LLM_API_KEY"));
        var configured = !string.IsNullOrEmpty(provider) && !string.IsNullOrEmpty(apiKey);

        string persistence = _env.IsDevelopment() ? "env.local" : "process";
        if (Environment.GetEnvironmentVariable("VERCEL") == "1" || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VERCEL_ENV")))
            persistence = "vercel_only";

        return new LlmSettingsResponse(
            Configured: configured,
            Provider: string.IsNullOrEmpty(provider) ? null : provider,
            Model: string.IsNullOrEmpty(model) ? null : model,
            Mode: configured ? "live" : "demo",
            Persistence: persistence
        );
    }

    public LlmSettingsResponse Update(LlmSettingsRequest req)
    {
        // Hard-reset path: clearAll wipes provider, model, and key in one shot.
        // This is what /api/settings/llm tests use to return to a known
        // baseline; without it, the service would happily re-emit the last
        // configured value across process invocations.
        if (req.ClearAll == true)
        {
            Environment.SetEnvironmentVariable("LLM_PROVIDER", null);
            Environment.SetEnvironmentVariable("LLM_MODEL", null);
            Environment.SetEnvironmentVariable("LLM_API_KEY", null);
            return ReadStatus();
        }

        bool firstConfigure = string.IsNullOrEmpty(Norm(Environment.GetEnvironmentVariable("LLM_PROVIDER")))
                          && string.IsNullOrEmpty(Norm(Environment.GetEnvironmentVariable("LLM_API_KEY")));

        // Provider
        string? nextProvider = null;
        if (!string.IsNullOrEmpty(req.Provider))
        {
            if (!_supported.Contains(req.Provider))
                throw new ArgumentException("invalid_provider");
            nextProvider = req.Provider;
        }
        else if (firstConfigure) nextProvider = "openai";
        else nextProvider = Norm(Environment.GetEnvironmentVariable("LLM_PROVIDER"));

        // Model
        string? nextModel;
        if (!string.IsNullOrWhiteSpace(req.Model)) nextModel = req.Model.Trim();
        else if (firstConfigure && string.IsNullOrEmpty(Norm(Environment.GetEnvironmentVariable("LLM_MODEL")))) nextModel = "gpt-6-luna";
        else nextModel = Norm(Environment.GetEnvironmentVariable("LLM_MODEL"));

        // Key
        string? trimmedKey = string.IsNullOrEmpty(req.ApiKey) ? null : req.ApiKey.Trim();

        if (req.ClearKey == true)
        {
            Environment.SetEnvironmentVariable("LLM_API_KEY", null);
        }
        else if (!string.IsNullOrEmpty(trimmedKey))
        {
            Environment.SetEnvironmentVariable("LLM_API_KEY", trimmedKey);
        }

        if (!string.IsNullOrEmpty(nextProvider)) Environment.SetEnvironmentVariable("LLM_PROVIDER", nextProvider);
        if (!string.IsNullOrEmpty(nextModel))    Environment.SetEnvironmentVariable("LLM_MODEL",    nextModel);

        return ReadStatus();
    }

    private static string Norm(string? s) => string.IsNullOrEmpty(s) ? "" : s.Trim();
}
