using System.Runtime.CompilerServices;
using ButlerDidIt.Ai;
using ButlerDidIt.Ai.Fake;
using ButlerDidIt.Game.Scenarios;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace ButlerDidIt.Ai.Tests;

/// <summary>Settings, usage and budget stand-ins so the gateway runs without a database.</summary>
public sealed class TestAi : IAiSettingsSource, IAiUsageSink, IAiBudget, IChatClientFactory
{
    public List<AiUsageRecord> Usage { get; } = [];
    public bool OverBudget { get; set; }
    public bool Configured { get; set; } = true;
    public IChatClient Client { get; set; } = new FakeChatClient();

    public Task<AiRoleSettings?> GetRoleAsync(AiRole role, CancellationToken ct) => Task.FromResult<AiRoleSettings?>(
        Configured ? new AiRoleSettings(role, new AiProviderSettings(Guid.Empty, "Test", AiProviderKind.Fake, null, null), "fake-model", null, null) : null);

    public Task RecordAsync(AiUsageRecord record, CancellationToken ct)
    {
        Usage.Add(record);
        return Task.CompletedTask;
    }

    public Task EnsureWithinBudgetAsync(string hostUserId, CancellationToken ct) =>
        OverBudget ? throw new AiBudgetExceededException("Over budget.") : Task.CompletedTask;

    public IChatClient Create(AiProviderSettings provider, string model) => Client;

    public AiGateway Gateway() => new(this, this, this, this, TimeProvider.System, NullLogger<AiGateway>.Instance);

    public static readonly AiCallContext Context = new("host-1");

    public static ThemeDefinition Theme => new() { Slug = "speakeasy", Name = "Death at the Gin Joint", Era = "1920s" };
}

/// <summary>Replies from a fixed script, one reply per call, recording what it was sent.</summary>
public sealed class ScriptedChatClient(params string[] replies) : IChatClient
{
    private int _next;
    public List<List<ChatMessage>> Calls { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls.Add(messages.ToList());
        var reply = replies[Math.Min(_next++, replies.Length - 1)];
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply))
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var u in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) yield return u;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
