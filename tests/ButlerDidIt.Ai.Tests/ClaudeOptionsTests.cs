using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using ButlerDidIt.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace ButlerDidIt.Ai.Tests;

/// <summary>
/// Claude-specific options (#63): per-role effort, and a fallback model when a request is declined.
/// No API key is needed: the Anthropic client sends to a stand-in transport that records every request
/// body and answers from a script, so these tests prove the exact wire shape. Whether real models
/// accept it (and get faster at low effort) is the live check in docs/verifying-providers.md.
/// </summary>
public class ClaudeOptionsTests
{
    private static readonly AiProviderSettings Claude = new(Guid.NewGuid(), "Claude", AiProviderKind.Anthropic, null, "sk-ant-test");

    /// <summary>A Messages API reply, as Claude sends it.</summary>
    private static string Message(string model, string stopReason, string text) => new JsonObject
    {
        ["id"] = "msg_1",
        ["type"] = "message",
        ["role"] = "assistant",
        ["model"] = model,
        ["content"] = text.Length == 0 ? new JsonArray() : new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["stop_reason"] = stopReason,
        ["stop_sequence"] = null,
        ["usage"] = new JsonObject { ["input_tokens"] = 12, ["output_tokens"] = 3 },
    }.ToJsonString();

    private static async Task<(ChatResponse Response, List<JsonObject> Sent)> AskAsync(AiRoleSettings role, params string[] replies)
    {
        var transport = new RecordingTransport(replies);
        var factory = new ChatClientFactory(allowFake: false) { AnthropicTransport = transport };
        using var client = factory.Create(role.Provider, role.Model, role.RefusalFallbackModel);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello?")], AiGateway.BuildOptions(role, 256, jsonOutput: false));
        return (response, transport.Bodies);
    }

    [Fact]
    public async Task A_role_effort_is_sent_as_output_config_effort()
    {
        var (response, sent) = await AskAsync(new AiRoleSettings(AiRole.Actor, Claude, "claude-opus-5-5", null, null, Effort: "low"),
            Message("claude-opus-5-5", "end_turn", "Good evening."));

        Assert.Equal("Good evening.", response.Text);
        var body = Assert.Single(sent);
        Assert.Equal("low", body["output_config"]?["effort"]?.GetValue<string>());
        Assert.Equal("claude-opus-5-5", body["model"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("medium", "medium")]
    [InlineData("HIGH", "high")]
    [InlineData("xhigh", "xhigh")]
    public async Task Every_effort_level_reaches_the_request(string configured, string sent)
    {
        var (_, bodies) = await AskAsync(new AiRoleSettings(AiRole.Storyteller, Claude, "claude-opus-5-5", null, null, Effort: configured),
            Message("claude-opus-5-5", "end_turn", "OK"));
        Assert.Equal(sent, Assert.Single(bodies)["output_config"]?["effort"]?.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("turbo")]
    public async Task No_effort_or_an_unknown_one_leaves_the_models_default(string? effort)
    {
        var (_, sent) = await AskAsync(new AiRoleSettings(AiRole.Actor, Claude, "claude-opus-5-5", null, null, Effort: effort),
            Message("claude-opus-5-5", "end_turn", "OK"));
        Assert.Null(Assert.Single(sent)["output_config"]?["effort"]);
    }

    [Fact]
    public void Other_providers_never_get_an_effort()
    {
        var openAi = new AiRoleSettings(AiRole.Actor, Claude with { Kind = AiProviderKind.OpenAI }, "gpt-5", null, null, Effort: "low");
        Assert.Null(AiGateway.BuildOptions(openAi, 256, jsonOutput: false).Reasoning);
    }

    [Fact]
    public async Task A_declined_request_is_retried_on_the_fallback_model()
    {
        var role = new AiRoleSettings(AiRole.Storyteller, Claude, "claude-opus-5-5", null, null, RefusalFallbackModel: "claude-opus-4-8");
        var (response, sent) = await AskAsync(role,
            Message("claude-opus-5-5", "refusal", ""),
            Message("claude-opus-4-8", "end_turn", "A room of mirrors…"));

        Assert.Equal(2, sent.Count);
        Assert.Equal("claude-opus-5-5", sent[0]["model"]?.GetValue<string>());
        Assert.Equal("claude-opus-4-8", sent[1]["model"]?.GetValue<string>());
        Assert.Contains("A room of mirrors", response.Text);
    }

    [Fact]
    public async Task Without_a_fallback_a_declined_request_says_so_instead_of_failing_as_empty()
    {
        var ai = new RefusingAi();
        var gateway = new AiGateway(ai, ai, ai, ai, TimeProvider.System, NullLogger<AiGateway>.Instance);
        var error = await Assert.ThrowsAsync<AiCallFailedException>(() =>
            gateway.CompleteAsync(AiRole.Actor, "You are a butler.", [new ChatMessage(ChatRole.User, "Hello")], TestAi.Context));

        Assert.Contains("declined this request", error.Message);
        Assert.Contains("fallback model for the Actor role", error.Message);
        var usage = Assert.Single(ai.Usage);
        Assert.False(usage.Success);
        Assert.Contains("refusal", usage.Error);
    }

    [Fact]
    public void Effort_levels_are_normalized_for_storage()
    {
        Assert.Equal("xhigh", AiEffort.Normalize(" XHigh "));
        Assert.Null(AiEffort.Normalize("turbo"));
        Assert.Null(AiEffort.Normalize(null));
        Assert.Equal(["low", "medium", "high", "xhigh"], AiEffort.Levels);
    }

    /// <summary>Answers each request with the next scripted reply, and keeps a copy of every JSON body sent.</summary>
    private sealed class RecordingTransport(string[] replies) : HttpMessageHandler
    {
        private int _next;
        public List<JsonObject> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
            var reply = replies[Math.Min(_next++, replies.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>A Claude role whose model always declines (stop_reason "refusal" arrives as ContentFilter).</summary>
    private sealed class RefusingAi : IAiSettingsSource, IAiUsageSink, IAiBudget, IChatClientFactory, IChatClient
    {
        public List<AiUsageRecord> Usage { get; } = [];
        public Task<AiRoleSettings?> GetRoleAsync(AiRole role, CancellationToken ct) =>
            Task.FromResult<AiRoleSettings?>(new AiRoleSettings(role, Claude, "claude-opus-5-5", null, null));
        public Task RecordAsync(AiUsageRecord record, CancellationToken ct) { Usage.Add(record); return Task.CompletedTask; }
        public Task EnsureWithinBudgetAsync(string hostUserId, CancellationToken ct) => Task.CompletedTask;
        public IChatClient Create(AiProviderSettings provider, string model) => this;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")) { FinishReason = ChatFinishReason.ContentFilter });

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var u in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) yield return u;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
