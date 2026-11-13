using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glossator.AI.Configuration;
using Glossator.Core.Data;
using Glossator.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Glossator.App.Infrastructure;

public sealed class UserSettingsService(
    IOptionsMonitor<GlossatorAiOptions> aiOptionsMonitor,
    IConfiguration configuration,
    IExchangeRateService? exchangeRateService = null) : IDisposable
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private ExchangeRateQuote? _lastQuote;
    private bool _manualOverrideActive;

    public GlossatorAiOptions CurrentOptions => aiOptionsMonitor.CurrentValue;

    public ExchangeRateQuote? LastExchangeRateQuote => _lastQuote;

    public bool IsDarkThemePreferred
        => !string.Equals(configuration["Studio:Theme"], "Light", StringComparison.OrdinalIgnoreCase);

    public string OtelEndpoint
    {
        get
        {
            var val = configuration["OpenTelemetry:Endpoint"];
            return string.IsNullOrWhiteSpace(val) ? "http://localhost:4317" : val;
        }
    }

    public string OtelServiceName
    {
        get
        {
            var configured = configuration["OpenTelemetry:ServiceName"];
            return string.IsNullOrWhiteSpace(configured) ||
                   configured.Equals("Glossator.Cli", StringComparison.OrdinalIgnoreCase)
                ? "Glossator.Studio"
                : configured;
        }
    }

    public double UsdToTryRate
    {
        get
        {
            if (_lastQuote is { IsLive: true } live && !_manualOverrideActive)
            {
                return live.Rate;
            }

            return ConfiguredFallbackRate;
        }
    }

    public async Task<ExchangeRateQuote> RefreshUsdToTryRateAsync(
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        var fallback = ConfiguredFallbackRate;
        if (_manualOverrideActive && !forceRefresh)
        {
            return new ExchangeRateQuote(fallback, "Manuel Ayar", IsLive: false, DateTimeOffset.UtcNow);
        }

        if (exchangeRateService == null)
        {
            var localQuote = new ExchangeRateQuote(fallback, "Yerel Yedek", IsLive: false, DateTimeOffset.UtcNow);
            _lastQuote = localQuote;
            return localQuote;
        }

        var quote = await exchangeRateService.GetUsdToTryRateAsync(fallback, forceRefresh, ct);
        _lastQuote = quote;

        if (quote.IsLive)
        {
            _manualOverrideActive = false;
            var rounded = Math.Round(quote.Rate, 4);
            configuration["Studio:UsdToTryRate"] = rounded.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (Math.Abs(rounded - fallback) >= 0.0001)
            {
                await PersistStudioUsdToTryRateAsync(rounded, ct);
            }
        }

        return quote;
    }

    private double ConfiguredFallbackRate =>
        double.TryParse(configuration["Studio:UsdToTryRate"], System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
        double.IsFinite(parsed) && parsed > 0
            ? parsed
            : ExchangeRateService.DefaultFallbackRate;

    private async Task PersistStudioUsdToTryRateAsync(double rate, CancellationToken ct)
    {
        await _ioLock.WaitAsync(ct);
        try
        {
            var rootObj = await ReadOrCreateRootAsync(ct);
            var studioObj = (rootObj["Studio"] as JsonObject) ?? new JsonObject();
            rootObj["Studio"] = studioObj;
            studioObj["UsdToTryRate"] = rate;
            await WriteRootAsync(rootObj, ct);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task SaveAiSettingsAsync(
        string activeProvider,
        Dictionary<string, AiProviderOptions> providers,
        Dictionary<string, AgentRoleOptions> agents,
        CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct);
        try
        {
            var live = aiOptionsMonitor.CurrentValue;
            live.ActiveProvider = activeProvider;

            var removedKeys = live.Providers.Keys.Where(k => !providers.ContainsKey(k)).ToList();
            foreach (var rk in removedKeys)
            {
                live.Providers.Remove(rk);
            }

            foreach (var (key, val) in providers)
            {
                if (!live.Providers.TryGetValue(key, out var existing))
                {
                    existing = new AiProviderOptions();
                    live.Providers[key] = existing;
                }

                existing.Endpoint = val.Endpoint;
                existing.ApiKey = val.ApiKey;
                existing.DefaultModel = val.DefaultModel;
            }

            foreach (var (role, val) in agents)
            {
                if (!live.Agents.TryGetValue(role, out var existing))
                {
                    existing = new AgentRoleOptions();
                    live.Agents[role] = existing;
                }

                existing.Provider = val.Provider;
                existing.ModelId = val.ModelId;
                existing.ReasoningEffort = string.IsNullOrWhiteSpace(val.ReasoningEffort) ? string.Empty : val.ReasoningEffort;
            }

            var rootObj = await ReadOrCreateRootAsync(ct);

            var aiObj = (rootObj["GlossatorAi"] as JsonObject) ?? new JsonObject();
            rootObj["GlossatorAi"] = aiObj;
            aiObj["ActiveProvider"] = activeProvider;

            var providersObj = new JsonObject();
            aiObj["Providers"] = providersObj;
            foreach (var (key, val) in live.Providers)
            {
                var pObj = new JsonObject
                {
                    ["Endpoint"] = val.Endpoint,
                    ["ApiKey"] = val.ApiKey,
                    ["DefaultModel"] = val.DefaultModel
                };
                providersObj[key] = pObj;
            }

            var agentsObj = (aiObj["Agents"] as JsonObject) ?? new JsonObject();
            aiObj["Agents"] = agentsObj;
            foreach (var (role, val) in live.Agents)
            {
                var aObj = (agentsObj[role] as JsonObject) ?? new JsonObject();
                aObj["Provider"] = val.Provider;
                aObj["ModelId"] = val.ModelId;
                // Boş string yazılır ki appsettings.json içindeki varsayılan ReasoningEffort yeniden dirilmesin
                aObj["ReasoningEffort"] = string.IsNullOrWhiteSpace(val.ReasoningEffort) ? "" : val.ReasoningEffort;
                agentsObj[role] = aObj;
            }

            await WriteRootAsync(rootObj, ct);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task SaveStudioPreferencesAsync(
        string otelEndpoint,
        string otelServiceName,
        double usdToTryRate,
        CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct);
        try
        {
            var rootObj = await ReadOrCreateRootAsync(ct);

            var otelObj = (rootObj["OpenTelemetry"] as JsonObject) ?? new JsonObject();
            rootObj["OpenTelemetry"] = otelObj;
            otelObj["Endpoint"] = otelEndpoint.Trim();
            otelObj["ServiceName"] = string.IsNullOrWhiteSpace(otelServiceName) ? "Glossator.Studio" : otelServiceName.Trim();

            var roundedRate = Math.Round(usdToTryRate, 4);
            var studioObj = (rootObj["Studio"] as JsonObject) ?? new JsonObject();
            rootObj["Studio"] = studioObj;
            studioObj["UsdToTryRate"] = roundedRate;

            configuration["OpenTelemetry:Endpoint"] = otelEndpoint.Trim();
            configuration["OpenTelemetry:ServiceName"] = string.IsNullOrWhiteSpace(otelServiceName) ? "Glossator.Studio" : otelServiceName.Trim();
            configuration["Studio:UsdToTryRate"] = roundedRate.ToString(System.Globalization.CultureInfo.InvariantCulture);

            _manualOverrideActive = true;
            _lastQuote = new ExchangeRateQuote(roundedRate, "Manuel Ayar", IsLive: false, DateTimeOffset.UtcNow);

            await WriteRootAsync(rootObj, ct);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task SaveThemePreferenceAsync(bool isDark, CancellationToken ct = default)
    {
        var themeName = isDark ? "Dark" : "Light";
        configuration["Studio:Theme"] = themeName;

        await _ioLock.WaitAsync(ct);
        try
        {
            var rootObj = await ReadOrCreateRootAsync(ct);
            var studioObj = (rootObj["Studio"] as JsonObject) ?? new JsonObject();
            rootObj["Studio"] = studioObj;
            studioObj["Theme"] = themeName;

            await WriteRootAsync(rootObj, ct);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private static async Task<JsonObject> ReadOrCreateRootAsync(CancellationToken ct)
    {
        var settingsPath = GlossatorPaths.GetUserSettingsPath();
        if (!File.Exists(settingsPath)) return new JsonObject();

        try
        {
            var existingJson = await File.ReadAllTextAsync(settingsPath, ct);
            return (JsonNode.Parse(existingJson) as JsonObject) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static async Task WriteRootAsync(JsonObject rootObj, CancellationToken ct)
    {
        var settingsPath = GlossatorPaths.GetUserSettingsPath();
        await AtomicFileWriter.WriteAllTextAsync(settingsPath, rootObj.ToJsonString(WriteOptions), ct);
    }

    public void Dispose() => _ioLock.Dispose();
}
