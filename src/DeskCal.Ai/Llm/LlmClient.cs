using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace DeskCal.Ai.Llm;

public sealed record LlmMessage(string Role, string Content)
{
    public static LlmMessage System(string text) => new("system", text);
    public static LlmMessage User(string text) => new("user", text);
}

/// <param name="JsonSchema">When given, the server is asked to answer with JSON of exactly this shape.</param>
public sealed record LlmRequest(IReadOnlyList<LlmMessage> Messages, JsonNode? JsonSchema = null, int MaxTokens = 400, double Temperature = 0, int ContextTokens = 4096);

public sealed record LlmResponse(string Text, TimeSpan Elapsed, int? PromptTokens, int? OutputTokens);

public enum LlmFailure { NotConfigured, Unreachable, Timeout, Http, BadResponse, TooLarge, Redirected }

/// <summary>Anything that goes wrong talking to the model. The message is written for the user and never contains calendar text.</summary>
public sealed class LlmException(LlmFailure kind, string message, int? status = null) : Exception(message)
{
    public LlmFailure Kind { get; } = kind;
    public int? Status { get; } = status;
}

/// <param name="Ok">Ready to use: the server answered and the chosen model is installed. A connection with no model chosen yet is not Ok, but <see cref="Models"/> is filled.</param>
public sealed record ConnectionReport(bool Ok, string Message, IReadOnlyList<string> Models, bool DataLeavesPc);

public interface ILlmClient
{
    string Model { get; }

    Task<LlmResponse> ChatAsync(LlmRequest request, CancellationToken ct);

    /// <summary>Names of the models the server offers.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);

    /// <summary>Ask the server to load the model now, so that the real request does not pay for the loading.</summary>
    Task WarmUpAsync(CancellationToken ct);

    /// <summary>
    /// One vector per text, each scaled to length 1 (so that the dot product of two is their cosine similarity). Throws
    /// <see cref="LlmException"/> (BadResponse) when the server answers with the wrong number of vectors, empty or differently sized
    /// vectors, or numbers that are not finite. Uses this client's <see cref="Model"/>: make the client with the embedding model.
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
}

internal static class Vectors
{
    /// <summary>Checks a server's answer and scales every vector to length 1.</summary>
    public static IReadOnlyList<float[]> Normalized(IReadOnlyList<float[]> raw, int expected)
    {
        if (raw.Count != expected) throw new LlmException(LlmFailure.BadResponse, "AI 서버가 요청한 개수만큼의 임베딩을 주지 않았습니다.");
        int dim = raw.Count == 0 ? 0 : raw[0].Length;
        var result = new List<float[]>(raw.Count);
        foreach (var v in raw)
        {
            if (v.Length == 0 || v.Length != dim) throw new LlmException(LlmFailure.BadResponse, "AI 서버가 준 임베딩의 크기가 올바르지 않습니다.");
            double sum = 0;
            foreach (float x in v)
            {
                if (!float.IsFinite(x)) throw new LlmException(LlmFailure.BadResponse, "AI 서버가 준 임베딩에 올바르지 않은 숫자가 있습니다.");
                sum += (double)x * x;
            }
            if (sum <= 0) throw new LlmException(LlmFailure.BadResponse, "AI 서버가 준 임베딩이 모두 0 입니다.");
            float inv = (float)(1.0 / Math.Sqrt(sum));
            var n = new float[v.Length];
            for (int i = 0; i < v.Length; i++) n[i] = v[i] * inv;
            result.Add(n);
        }
        return result;
    }

    public static float Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        float s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}

/// <summary>What both clients share: one HTTP client that never follows redirects, a time limit, a size limit, and plain error messages.</summary>
internal sealed class LlmHttp : IDisposable
{
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public Uri Base { get; }

    public LlmHttp(Uri baseUri, TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        Base = baseUri;
        _timeout = timeout;
        handler ??= new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5) };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };    // the limit is applied per call below
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("M-DeskCal-AI/1.0");
    }

    /// <summary>Relative to <see cref="Base"/>; a path that repeats the base's own "/v1/" is not doubled.</summary>
    public Uri Url(string path)
    {
        path = path.TrimStart('/');
        string basePath = Base.AbsolutePath;
        if (basePath.EndsWith("/v1/", StringComparison.Ordinal) && path.StartsWith("v1/", StringComparison.Ordinal)) path = path[3..];
        return new Uri(Base, path);
    }

    public async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct, Func<int, string, bool>? retryable = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            var (status, text) = await SendOnceAsync(method, path, body, ct);
            if (status is >= 300 and < 400) throw new LlmException(LlmFailure.Redirected, "서버가 다른 주소로 보내려 해서 중단했습니다 (리디렉션은 따르지 않습니다).", status);
            if (status is < 200 or >= 300)
            {
                if (attempt == 0 && retryable?.Invoke(status, text) == true) continue;
                throw new LlmException(LlmFailure.Http, $"AI 서버가 오류로 응답했습니다 (HTTP {status}). 모델 이름과 주소를 확인하세요.", status);
            }
            JsonNode? node;
            try { node = JsonNode.Parse(text); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException) { throw new LlmException(LlmFailure.BadResponse, "AI 서버의 응답을 읽을 수 없습니다 (JSON 이 아닙니다)."); }
            return node as JsonObject ?? throw new LlmException(LlmFailure.BadResponse, "AI 서버의 응답 형식이 예상과 다릅니다.");
        }
    }

    private async Task<(int Status, string Text)> SendOnceAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            using var req = new HttpRequestMessage(method, Url(path));
            if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (resp.Content.Headers.ContentLength > MaxResponseBytes) throw new LlmException(LlmFailure.TooLarge, "AI 서버의 응답이 너무 큽니다.");
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cts.Token)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxResponseBytes) throw new LlmException(LlmFailure.TooLarge, "AI 서버의 응답이 너무 큽니다.");
            }
            return ((int)resp.StatusCode, Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException(LlmFailure.Timeout, $"AI 서버가 {(int)_timeout.TotalSeconds}초 안에 응답하지 않았습니다. 모델을 처음 불러오는 중이면 잠시 뒤 다시 시도하세요.");
        }
        catch (HttpRequestException)
        {
            throw new LlmException(LlmFailure.Unreachable, "AI 서버에 연결할 수 없습니다. 서버(예: Ollama)가 켜져 있는지, 주소가 맞는지 확인하세요.");
        }
    }

    public void Dispose() => _http.Dispose();
}

public static class LlmClientFactory
{
    /// <summary>The client for these settings; throws <see cref="LlmException"/> (NotConfigured) when the address is not allowed or no model is chosen.</summary>
    public static ILlmClient Create(AiSettings settings, HttpMessageHandler? handler = null, bool requireModel = true)
    {
        var s = settings.Sanitized();
        if (!EndpointPolicy.TryParse(s.Endpoint, s.AllowRemote, out var uri, out var error))
            throw new LlmException(LlmFailure.NotConfigured, error!);
        if (requireModel && s.Model.Length == 0)
            throw new LlmException(LlmFailure.NotConfigured, "사용할 모델을 고르세요 (설정 > AI).");
        var http = new LlmHttp(uri!, TimeSpan.FromSeconds(s.TimeoutSeconds), handler);
        return s.Provider == AiProvider.Ollama ? new OllamaClient(http, s) : new OpenAiCompatClient(http, s);
    }

    /// <summary>Connect, list the models and say what happened, in words for the settings page.</summary>
    public static async Task<ConnectionReport> TestAsync(AiSettings settings, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        var s = settings.Sanitized();
        if (!EndpointPolicy.TryParse(s.Endpoint, s.AllowRemote, out var uri, out var error)) return new(false, error!, [], false);
        try
        {
            var client = Create(s, handler, requireModel: false);
            var models = await client.ListModelsAsync(ct);
            string note = models.Count == 0 ? "연결되었지만 설치된 모델이 없습니다."
                : s.Model.Length == 0 ? $"연결되었습니다. 모델 {models.Count}개 — 사용할 모델을 고르세요."
                : models.Contains(s.Model) ? $"연결되었습니다. 모델 '{s.Model}' 을 쓸 수 있습니다."
                : $"연결되었지만 모델 '{s.Model}' 이 서버에 없습니다. 목록에서 고르세요.";
            return new(models.Contains(s.Model), note, models, EndpointPolicy.DataLeavesPc(uri!));      // Ok = ready to use: connected AND the chosen model exists
        }
        catch (LlmException ex) { return new(false, ex.Message, [], EndpointPolicy.DataLeavesPc(uri!)); }
    }
}
