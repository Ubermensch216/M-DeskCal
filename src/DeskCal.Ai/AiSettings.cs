using DeskCal.Data;

namespace DeskCal.Ai;

public enum AiProvider { Ollama = 0, OpenAiCompatible = 1 }

/// <summary>
/// How the app talks to a language model. Everything is off until the user switches it on, and the address may only point at
/// this computer unless <see cref="AllowRemote"/> is set (then the data leaves the PC, and the settings page says so).
/// </summary>
public sealed record AiSettings
{
    public const string OllamaEndpoint = "http://127.0.0.1:11434";
    public const string OpenAiCompatEndpoint = "http://127.0.0.1:1234";

    public bool Enabled { get; init; }
    public AiProvider Provider { get; init; } = AiProvider.Ollama;
    public string Endpoint { get; init; } = OllamaEndpoint;
    /// <summary>The model name as the server knows it (for example "gemma4:e2b"). Empty = not chosen yet.</summary>
    public string Model { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 60;
    /// <summary>How long Ollama keeps the model in memory after the last request ("5m", "30m", "0" = unload at once).</summary>
    public string KeepAlive { get; init; } = "5m";
    /// <summary>Allow an address that is not on this computer. Off by default: the calendar's text would be sent to that server.</summary>
    public bool AllowRemote { get; init; }
    /// <summary>The embedding model for meaning-based search (for example "bge-m3"). Empty = not chosen yet.</summary>
    public string EmbedModel { get; init; } = "";
    /// <summary>Meaning-based search in the search window. Off by default; it keeps an index of the calendar's text on this computer.</summary>
    public bool SemanticSearch { get; init; }

    public static string DefaultEndpoint(AiProvider p) => p == AiProvider.Ollama ? OllamaEndpoint : OpenAiCompatEndpoint;

    public AiSettings Sanitized() => this with
    {
        Endpoint = string.IsNullOrWhiteSpace(Endpoint) ? DefaultEndpoint(Provider) : Endpoint.Trim(),
        Model = (Model ?? "").Trim(),
        EmbedModel = (EmbedModel ?? "").Trim(),
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 5, 600),
        KeepAlive = ValidKeepAlive(KeepAlive) ? KeepAlive.Trim() : "5m",
    };

    /// <summary>"0", "30s", "5m", "2h" or "-1" (keep for ever).</summary>
    public static bool ValidKeepAlive(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text is "0" or "-1") return true;
        if (text.Length < 2 || text[^1] is not ('s' or 'm' or 'h')) return false;
        if (!int.TryParse(text[..^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n)) return false;
        return text[^1] switch { 's' => n is >= 1 and <= 86_400, 'm' => n is >= 1 and <= 1_440, _ => n is >= 1 and <= 24 };     // at most one day
    }
}

/// <summary>Reads and writes <see cref="AiSettings"/> as "ai.*" keys of the settings table.</summary>
public sealed class AiSettingsStore(SettingsStore store)
{
    private const string P = "ai.";

    public AiSettings Load()
    {
        var d = new AiSettings();
        var provider = store.GetInt(P + "provider", (int)d.Provider);
        return new AiSettings
        {
            Enabled = store.GetBool(P + "enabled", d.Enabled),
            Provider = Enum.IsDefined((AiProvider)provider) ? (AiProvider)provider : d.Provider,
            Endpoint = store.GetString(P + "endpoint") ?? d.Endpoint,
            Model = store.GetString(P + "model") ?? d.Model,
            TimeoutSeconds = store.GetInt(P + "timeout", d.TimeoutSeconds),
            KeepAlive = store.GetString(P + "keep_alive") ?? d.KeepAlive,
            AllowRemote = store.GetBool(P + "allow_remote", d.AllowRemote),
            EmbedModel = store.GetString(P + "embed_model") ?? d.EmbedModel,
            SemanticSearch = store.GetBool(P + "semantic", d.SemanticSearch),
        }.Sanitized();
    }

    public void Save(AiSettings settings)
    {
        var s = settings.Sanitized();
        store.SetBool(P + "enabled", s.Enabled);
        store.SetInt(P + "provider", (int)s.Provider);
        store.SetString(P + "endpoint", s.Endpoint);
        store.SetString(P + "model", s.Model);
        store.SetInt(P + "timeout", s.TimeoutSeconds);
        store.SetString(P + "keep_alive", s.KeepAlive);
        store.SetBool(P + "allow_remote", s.AllowRemote);
        store.SetString(P + "embed_model", s.EmbedModel);
        store.SetBool(P + "semantic", s.SemanticSearch);
    }
}
