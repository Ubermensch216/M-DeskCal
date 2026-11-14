using System.Diagnostics;
using System.Text.Json.Nodes;

namespace DeskCal.Ai.Llm;

/// <summary>Reads JSON values without throwing when a server sends something of the wrong type.</summary>
internal static class J
{
    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    /// <summary>A JSON array of numbers as floats; null when it is not one.</summary>
    public static float[]? Floats(JsonNode? n)
    {
        if (n is not JsonArray a) return null;
        var result = new float[a.Count];
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] is not JsonValue v || !v.TryGetValue<double>(out double d)) return null;
            result[i] = (float)d;
        }
        return result;
    }
}

/// <summary>Ollama's own API (/api/chat, /api/tags). Structured output through the "format" field, thinking switched off.</summary>
internal sealed class OllamaClient(LlmHttp http, AiSettings settings) : ILlmClient
{
    public string Model => settings.Model;

    public async Task<LlmResponse> ChatAsync(LlmRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var body = Body(request, think: false);
        // a model that cannot "think" may refuse the flag: ask once more without it
        var json = await http.SendAsync(HttpMethod.Post, "api/chat", body, ct, retryable: (status, _) =>
        {
            if (status != 400) return false;
            body.Remove("think");
            return true;
        });
        string? text = J.Str(json["message"]?["content"]);
        if (text is null) throw new LlmException(LlmFailure.BadResponse, "AI 서버의 응답에 내용이 없습니다.");
        return new LlmResponse(text, sw.Elapsed, J.Int(json["prompt_eval_count"]), J.Int(json["eval_count"]));
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        var json = await http.SendAsync(HttpMethod.Get, "api/tags", null, ct);
        return (json["models"] as JsonArray)?.Select(m => J.Str(m?["name"])).OfType<string>().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
               ?? throw new LlmException(LlmFailure.BadResponse, "AI 서버가 모델 목록을 주지 않았습니다 (Ollama 주소가 맞나요?).");
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0) return [];
        var body = new JsonObject
        {
            ["model"] = Model, ["input"] = new JsonArray(texts.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
            ["truncate"] = true, ["keep_alive"] = KeepAlive(),
        };
        var json = await http.SendAsync(HttpMethod.Post, "api/embed", body, ct);
        var raw = (json["embeddings"] as JsonArray)?.Select(e => J.Floats(e) ?? throw new LlmException(LlmFailure.BadResponse, "AI 서버가 준 임베딩을 읽을 수 없습니다.")).ToList()
                  ?? throw new LlmException(LlmFailure.BadResponse, "AI 서버의 응답에 임베딩이 없습니다 (임베딩 모델 이름과 Ollama 버전을 확인하세요).");
        return Vectors.Normalized(raw, texts.Count);
    }

    /// <summary>An empty message list makes Ollama load the model and keep it for <c>keep_alive</c>.</summary>
    public async Task WarmUpAsync(CancellationToken ct) =>
        await http.SendAsync(HttpMethod.Post, "api/chat", new JsonObject { ["model"] = Model, ["messages"] = new JsonArray(), ["stream"] = false, ["keep_alive"] = KeepAlive() }, ct);

    /// <summary>Ollama wants a number for "0" and "-1" and a duration string ("5m") otherwise.</summary>
    private JsonNode KeepAlive() =>
        settings.KeepAlive is "0" or "-1" ? JsonValue.Create(int.Parse(settings.KeepAlive))! : JsonValue.Create(settings.KeepAlive)!;

    private JsonObject Body(LlmRequest r, bool think)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = new JsonArray(r.Messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["stream"] = false,
            ["keep_alive"] = KeepAlive(),
            ["options"] = new JsonObject { ["temperature"] = r.Temperature, ["num_ctx"] = r.ContextTokens, ["num_predict"] = r.MaxTokens },
        };
        if (!think) body["think"] = false;
        if (r.JsonSchema is not null) body["format"] = r.JsonSchema.DeepClone();
        return body;
    }
}

/// <summary>The OpenAI-style API (/v1/chat/completions, /v1/models) that LM Studio, llama.cpp's server, vLLM and Ollama's /v1 all speak.</summary>
internal sealed class OpenAiCompatClient(LlmHttp http, AiSettings settings) : ILlmClient
{
    public string Model => settings.Model;

    public async Task<LlmResponse> ChatAsync(LlmRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var body = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = new JsonArray(request.Messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens,
            ["stream"] = false,
        };
        if (request.JsonSchema is not null)
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "result", ["strict"] = true, ["schema"] = request.JsonSchema.DeepClone() },
            };
        var json = await http.SendAsync(HttpMethod.Post, "v1/chat/completions", body, ct);
        string? text = J.Str((json["choices"] as JsonArray)?.FirstOrDefault()?["message"]?["content"]);
        if (text is null) throw new LlmException(LlmFailure.BadResponse, "AI 서버의 응답에 내용이 없습니다.");
        return new LlmResponse(text, sw.Elapsed, J.Int(json["usage"]?["prompt_tokens"]), J.Int(json["usage"]?["completion_tokens"]));
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        var json = await http.SendAsync(HttpMethod.Get, "v1/models", null, ct);
        return (json["data"] as JsonArray)?.Select(m => J.Str(m?["id"])).OfType<string>().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
               ?? throw new LlmException(LlmFailure.BadResponse, "AI 서버가 모델 목록을 주지 않았습니다.");
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0) return [];
        var body = new JsonObject { ["model"] = Model, ["input"] = new JsonArray(texts.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()) };
        var json = await http.SendAsync(HttpMethod.Post, "v1/embeddings", body, ct);
        var data = (json["data"] as JsonArray)?.OfType<JsonObject>().ToList() ?? throw new LlmException(LlmFailure.BadResponse, "AI 서버의 응답에 임베딩이 없습니다.");
        // the servers number their answers: put them back in the order of the request
        var ordered = data.OrderBy(d => J.Int(d["index"]) ?? 0).Select(d => J.Floats(d["embedding"]) ?? throw new LlmException(LlmFailure.BadResponse, "AI 서버가 준 임베딩을 읽을 수 없습니다.")).ToList();
        return Vectors.Normalized(ordered, texts.Count);
    }

    /// <summary>These servers load a model on the first request, so a one-token request is the warm-up.</summary>
    public async Task WarmUpAsync(CancellationToken ct) =>
        await http.SendAsync(HttpMethod.Post, "v1/chat/completions", new JsonObject
        {
            ["model"] = Model, ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "." }), ["max_tokens"] = 1, ["stream"] = false,
        }, ct);
}
