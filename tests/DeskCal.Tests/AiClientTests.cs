using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DeskCal.Ai;
using DeskCal.Ai.Llm;
using DeskCal.Data;

namespace DeskCal.Tests;

/// <summary>A language-model server in memory, reachable over real HTTP on this computer. Each test says how it answers.</summary>
internal sealed class FakeLlmServer : IDisposable
{
    public sealed record Seen(string Method, string Path, string Body);

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}";
    public List<Seen> Requests { get; } = [];
    public Func<Seen, (int Status, string Body, string? Location)> Respond { get; set; } = _ => (200, "{}", null);
    public TimeSpan Delay { get; set; }

    public FakeLlmServer()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); Port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            try
            {
                string body = new StreamReader(ctx.Request.InputStream, Encoding.UTF8).ReadToEnd();
                var seen = new Seen(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, body);
                lock (Requests) Requests.Add(seen);
                // Waits without holding a thread-pool thread (many fake servers run in parallel tests) and ends when the test disposes the server.
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, _stop.Token);
                var (status, text, location) = Respond(seen);
                var bytes = Encoding.UTF8.GetBytes(text);
                ctx.Response.StatusCode = status;
                if (location is not null) ctx.Response.AddHeader("Location", location);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch { try { ctx.Response.StatusCode = 500; } catch { } }
            finally { try { ctx.Response.Close(); } catch { } }
        }
    }

    public void Dispose() { _stop.Cancel(); try { _listener.Stop(); _listener.Close(); } catch { } }

    public static string OllamaChat(string content, int promptTokens = 100, int outTokens = 90) =>
        new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content }, ["prompt_eval_count"] = promptTokens, ["eval_count"] = outTokens }.ToJsonString();

    public static string OpenAiChat(string content) =>
        new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content } }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 80, ["completion_tokens"] = 40 },
        }.ToJsonString();
}

public class AiSettingsTests : IDisposable
{
    private readonly DbFixture _f = new();
    public void Dispose() => _f.Dispose();

    [Fact]
    public void AI_is_off_and_points_at_this_computer_until_the_user_says_otherwise()
    {
        var s = new AiSettingsStore(_f.Settings).Load();
        Assert.False(s.Enabled);
        Assert.False(s.AllowRemote);
        Assert.Equal("http://127.0.0.1:11434", s.Endpoint);
        Assert.Equal("", s.Model);
        Assert.Equal(AiProvider.Ollama, s.Provider);
    }

    [Fact]
    public void Settings_survive_a_round_trip_and_odd_stored_values_are_repaired()
    {
        var store = new AiSettingsStore(_f.Settings);
        store.Save(new AiSettings { Enabled = true, Provider = AiProvider.OpenAiCompatible, Endpoint = "http://localhost:1234", Model = "qwen3-4b", TimeoutSeconds = 90, KeepAlive = "30m" });
        var s = store.Load();
        Assert.Equal((true, AiProvider.OpenAiCompatible, "http://localhost:1234", "qwen3-4b", 90, "30m"), (s.Enabled, s.Provider, s.Endpoint, s.Model, s.TimeoutSeconds, s.KeepAlive));

        _f.Settings.SetInt("ai.timeout", 99999);
        _f.Settings.SetString("ai.keep_alive", "forever");
        _f.Settings.SetInt("ai.provider", 42);
        _f.Settings.SetString("ai.endpoint", "   ");
        var fixedUp = store.Load();
        Assert.Equal((600, "5m", AiProvider.Ollama, "http://127.0.0.1:11434"), (fixedUp.TimeoutSeconds, fixedUp.KeepAlive, fixedUp.Provider, fixedUp.Endpoint));
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("-1", true)]
    [InlineData("30s", true)]
    [InlineData("5m", true)]
    [InlineData("24h", true)]
    [InlineData("25h", false)]
    [InlineData("m", false)]
    [InlineData("5", false)]
    [InlineData("5 minutes", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Keep_alive_values(string? text, bool valid) => Assert.Equal(valid, AiSettings.ValidKeepAlive(text));

    [Fact]
    public void The_gate_needs_the_switch_on_and_the_calendar_unlocked()
    {
        var gate = new AiGate(_f.Settings);
        Assert.Contains("꺼져", gate.Refusal());
        Assert.True(gate.Unlocked);                                                    // the rule-based parser does not need the AI switch

        new AiSettingsStore(_f.Settings).Save(new AiSettings { Enabled = true });
        Assert.Null(gate.Refusal());

        new ContentLockStore(_f.Settings).Hidden = true;
        Assert.Contains("잠겨", gate.Refusal());
        Assert.False(gate.Unlocked);
    }
}

public class EndpointPolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://localhost:11434/")]
    [InlineData("http://[::1]:11434")]
    [InlineData("https://127.0.0.1:8443/v1")]
    public void Addresses_on_this_computer_are_allowed(string url)
    {
        Assert.True(EndpointPolicy.TryParse(url, allowRemote: false, out var uri, out var error), error);
        Assert.EndsWith("/", uri!.AbsolutePath);
        Assert.False(EndpointPolicy.DataLeavesPc(uri));
    }

    [Theory]
    [InlineData("http://192.168.0.5:11434")]
    [InlineData("https://api.example.com")]
    [InlineData("http://0.0.0.0:11434")]
    [InlineData("http://localhost.evil.example:11434")]
    public void Other_computers_are_refused_by_default(string url)
    {
        Assert.False(EndpointPolicy.TryParse(url, allowRemote: false, out var uri, out var error));
        Assert.Null(uri);
        Assert.Contains("전송", error);
    }

    [Theory]
    [InlineData("ftp://127.0.0.1/")]
    [InlineData("file:///C:/x")]
    [InlineData("http://user:secret@127.0.0.1:11434")]
    [InlineData("http://127.0.0.1:11434/?key=1")]
    [InlineData("http://127.0.0.1:11434/#x")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void Malformed_or_risky_addresses_are_refused_even_on_this_computer(string? url) =>
        Assert.False(EndpointPolicy.TryParse(url, allowRemote: true, out _, out _));

    [Fact]
    public void A_remote_address_needs_the_switch_and_https()
    {
        Assert.True(EndpointPolicy.TryParse("https://ai.example.com/", allowRemote: true, out var uri, out _));
        Assert.True(EndpointPolicy.DataLeavesPc(uri!));
        Assert.False(EndpointPolicy.TryParse("http://ai.example.com/", allowRemote: true, out _, out string? error));
        Assert.Contains("https", error);
    }
}

public class LlmClientTests
{
    private static AiSettings Local(FakeLlmServer s, AiProvider p = AiProvider.Ollama, string model = "gemma4:e2b", string keepAlive = "5m", string path = "") =>
        new() { Enabled = true, Provider = p, Endpoint = s.Url + path, Model = model, KeepAlive = keepAlive, TimeoutSeconds = 30 };

    private static LlmRequest Request(JsonNode? schema = null) =>
        new([LlmMessage.System("시스템"), LlmMessage.User("내일 3시 치과")], schema);

    [Fact]
    public async Task Ollama_lists_models_sorted_and_sends_the_chat_with_schema_thinking_off_and_keep_alive()
    {
        using var server = new FakeLlmServer();
        server.Respond = r => r.Path == "/api/tags"
            ? (200, """{"models":[{"name":"gemma4:e2b"},{"name":"bge-m3:latest"}]}""", null)
            : (200, FakeLlmServer.OllamaChat("{\"ok\":true}", 108, 94), null);
        var client = LlmClientFactory.Create(Local(server));

        Assert.Equal(["bge-m3:latest", "gemma4:e2b"], await client.ListModelsAsync(default));

        var schema = new JsonObject { ["type"] = "object" };
        var reply = await client.ChatAsync(Request(schema), default);
        Assert.Equal("{\"ok\":true}", reply.Text);
        Assert.Equal((108, 94), (reply.PromptTokens, reply.OutputTokens));

        var chat = JsonNode.Parse(server.Requests.Last(r => r.Path == "/api/chat").Body)!;
        Assert.Equal("gemma4:e2b", chat["model"]!.GetValue<string>());
        Assert.False(chat["stream"]!.GetValue<bool>());
        Assert.False(chat["think"]!.GetValue<bool>());
        Assert.Equal("5m", chat["keep_alive"]!.GetValue<string>());
        Assert.Equal("object", chat["format"]!["type"]!.GetValue<string>());
        Assert.Equal(0, chat["options"]!["temperature"]!.GetValue<double>());
        Assert.Equal("내일 3시 치과", chat["messages"]![1]!["content"]!.GetValue<string>());                // Korean arrives intact
    }

    [Fact]
    public async Task Ollama_keep_alive_zero_and_minus_one_are_numbers()
    {
        using var server = new FakeLlmServer();
        server.Respond = _ => (200, FakeLlmServer.OllamaChat("x"), null);
        await LlmClientFactory.Create(Local(server, keepAlive: "0")).ChatAsync(Request(), default);
        await LlmClientFactory.Create(Local(server, keepAlive: "-1")).ChatAsync(Request(), default);
        Assert.Equal([0, -1], server.Requests.Select(r => JsonNode.Parse(r.Body)!["keep_alive"]!.GetValue<int>()));
    }

    [Fact]
    public async Task A_model_that_cannot_think_is_asked_again_without_the_flag()
    {
        using var server = new FakeLlmServer();
        server.Respond = r => JsonNode.Parse(r.Body)!["think"] is not null ? (400, """{"error":"does not support thinking"}""", null) : (200, FakeLlmServer.OllamaChat("fine"), null);
        var reply = await LlmClientFactory.Create(Local(server)).ChatAsync(Request(), default);
        Assert.Equal("fine", reply.Text);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Warm_up_loads_the_model_with_an_empty_message_list()
    {
        using var server = new FakeLlmServer();
        server.Respond = _ => (200, """{"done":true}""", null);
        await LlmClientFactory.Create(Local(server)).WarmUpAsync(default);
        var body = JsonNode.Parse(server.Requests.Single().Body)!;
        Assert.Empty(body["messages"]!.AsArray());
        Assert.Equal("gemma4:e2b", body["model"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("/v1")]
    public async Task OpenAI_style_servers_get_json_schema_output_and_the_v1_prefix_is_not_doubled(string basePath)
    {
        using var server = new FakeLlmServer();
        server.Respond = r => r.Path.EndsWith("/models") ? (200, """{"data":[{"id":"qwen3-4b"},{"id":"gemma-3"}]}""", null) : (200, FakeLlmServer.OpenAiChat("{\"a\":1}"), null);
        var client = LlmClientFactory.Create(Local(server, AiProvider.OpenAiCompatible, "qwen3-4b", path: basePath));

        Assert.Equal(["gemma-3", "qwen3-4b"], await client.ListModelsAsync(default));
        var reply = await client.ChatAsync(Request(new JsonObject { ["type"] = "object" }), default);
        Assert.Equal(("{\"a\":1}", 80, 40), (reply.Text, reply.PromptTokens, reply.OutputTokens));

        Assert.All(server.Requests, r => Assert.StartsWith("/v1/", r.Path));
        Assert.DoesNotContain(server.Requests, r => r.Path.StartsWith("/v1/v1/"));
        var chat = JsonNode.Parse(server.Requests.Last().Body)!;
        Assert.Equal("json_schema", chat["response_format"]!["type"]!.GetValue<string>());
        Assert.Equal("qwen3-4b", chat["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenAI_style_warm_up_is_a_one_token_request()
    {
        using var server = new FakeLlmServer();
        server.Respond = _ => (200, FakeLlmServer.OpenAiChat("."), null);
        await LlmClientFactory.Create(Local(server, AiProvider.OpenAiCompatible)).WarmUpAsync(default);
        Assert.Equal(1, JsonNode.Parse(server.Requests.Single().Body)!["max_tokens"]!.GetValue<int>());
    }

    // ---- when things go wrong ----

    [Fact]
    public async Task A_server_that_is_not_running_is_reported_in_plain_words()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();       // a port nobody listens on
        var client = LlmClientFactory.Create(new AiSettings { Endpoint = $"http://127.0.0.1:{port}", Model = "m" });
        var ex = await Assert.ThrowsAsync<LlmException>(() => client.ChatAsync(Request(), default));
        Assert.Equal(LlmFailure.Unreachable, ex.Kind);
        Assert.Contains("연결할 수 없습니다", ex.Message);
    }

    [Fact]
    public async Task A_slow_server_times_out()
    {
        // The reply is far later than the timeout, so a timer that fires late on a busy machine still fires first.
        using var server = new FakeLlmServer { Delay = TimeSpan.FromSeconds(30) };
        server.Respond = _ => (200, FakeLlmServer.OllamaChat("late"), null);
        var http = new LlmHttp(new Uri(server.Url + "/"), TimeSpan.FromMilliseconds(300));
        var ex = await Assert.ThrowsAsync<LlmException>(() => new OllamaClient(http, new AiSettings { Model = "m" }).ChatAsync(Request(), default));
        Assert.Equal(LlmFailure.Timeout, ex.Kind);
    }

    [Fact]
    public async Task Cancelling_is_not_reported_as_a_timeout()
    {
        using var server = new FakeLlmServer { Delay = TimeSpan.FromSeconds(30) };     // never answers before the cancel
        server.Respond = _ => (200, FakeLlmServer.OllamaChat("late"), null);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LlmClientFactory.Create(Local(server)).ChatAsync(Request(), cts.Token));
    }

    [Fact]
    public async Task A_redirect_is_not_followed()
    {
        using var elsewhere = new FakeLlmServer();
        using var server = new FakeLlmServer();
        server.Respond = _ => (302, "", elsewhere.Url + "/api/chat");
        var ex = await Assert.ThrowsAsync<LlmException>(() => LlmClientFactory.Create(Local(server)).ChatAsync(Request(), default));
        Assert.Equal(LlmFailure.Redirected, ex.Kind);
        Assert.Empty(elsewhere.Requests);                                                           // the text never went to the other address
    }

    [Fact]
    public async Task An_error_status_is_reported_without_echoing_what_the_server_said()
    {
        using var server = new FakeLlmServer();
        server.Respond = _ => (500, "internal: SECRET-TRACE 내일 3시 치과", null);
        var ex = await Assert.ThrowsAsync<LlmException>(() => LlmClientFactory.Create(Local(server)).ChatAsync(Request(), default));
        Assert.Equal((LlmFailure.Http, 500), (ex.Kind, ex.Status));
        Assert.DoesNotContain("SECRET", ex.Message);
        Assert.DoesNotContain("치과", ex.Message);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"message":{"role":"assistant"}}""")]
    [InlineData("""{"message":{"content":42}}""")]
    [InlineData("")]
    public async Task A_reply_that_is_not_what_the_API_promises_is_a_bad_response(string body)
    {
        using var server = new FakeLlmServer();
        server.Respond = _ => (200, body, null);
        var ex = await Assert.ThrowsAsync<LlmException>(() => LlmClientFactory.Create(Local(server)).ChatAsync(Request(), default));
        Assert.Equal(LlmFailure.BadResponse, ex.Kind);
    }

    [Fact]
    public async Task A_huge_reply_is_refused()
    {
        using var server = new FakeLlmServer();
        server.Respond = _ => (200, "{\"x\":\"" + new string('a', LlmHttp.MaxResponseBytes + 10) + "\"}", null);
        var ex = await Assert.ThrowsAsync<LlmException>(() => LlmClientFactory.Create(Local(server)).ListModelsAsync(default));
        Assert.Equal(LlmFailure.TooLarge, ex.Kind);
    }

    [Fact]
    public void The_factory_refuses_other_computers_and_a_missing_model()
    {
        var remote = Assert.Throws<LlmException>(() => LlmClientFactory.Create(new AiSettings { Endpoint = "https://api.example.com", Model = "m" }));
        Assert.Equal(LlmFailure.NotConfigured, remote.Kind);
        var noModel = Assert.Throws<LlmException>(() => LlmClientFactory.Create(new AiSettings { Model = "" }));
        Assert.Equal(LlmFailure.NotConfigured, noModel.Kind);
        Assert.NotNull(LlmClientFactory.Create(new AiSettings { Model = "" }, requireModel: false));                       // listing models does not need one
        Assert.NotNull(LlmClientFactory.Create(new AiSettings { Endpoint = "https://api.example.com", Model = "m", AllowRemote = true }));
    }

    [Fact]
    public async Task The_connection_test_says_what_it_found()
    {
        using var server = new FakeLlmServer();
        server.Respond = _ => (200, """{"models":[{"name":"gemma4:e2b"},{"name":"bge-m3:latest"}]}""", null);

        var ok = await LlmClientFactory.TestAsync(Local(server), default);
        Assert.True(ok.Ok);
        Assert.Contains("gemma4:e2b", ok.Message);
        Assert.False(ok.DataLeavesPc);

        var none = await LlmClientFactory.TestAsync(Local(server, model: ""), default);
        Assert.False(none.Ok);                                                                 // connected, but nothing chosen yet
        Assert.Equal(2, none.Models.Count);
        Assert.Contains("고르세요", none.Message);

        var missing = await LlmClientFactory.TestAsync(Local(server, model: "llama3"), default);
        Assert.False(missing.Ok);
        Assert.Contains("서버에 없습니다", missing.Message);

        var refused = await LlmClientFactory.TestAsync(new AiSettings { Endpoint = "https://api.example.com" }, default);
        Assert.False(refused.Ok);
        Assert.Empty(server.Requests.Skip(3));                                                     // nothing was sent for the refused address
        Assert.Contains("전송", refused.Message);
    }
}
