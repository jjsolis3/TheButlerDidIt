using System.Diagnostics;
using System.Text.Json;
using ButlerDidIt.Game;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ButlerDidIt.Ai;

/// <summary>
/// The single door every AI request goes through. It:
///   1. looks up which provider and model the role uses,
///   2. checks the host's budget before spending anything,
///   3. makes the call (streamed, so long answers don't hit HTTP timeouts),
///   4. records tokens used, cost and duration, even when the call fails,
///   5. turns vendor errors into a friendly <see cref="AiException"/>.
/// </summary>
public sealed class AiGateway(
    IAiSettingsSource settings,
    IChatClientFactory factory,
    IAiUsageSink usage,
    IAiBudget budget,
    TimeProvider clock,
    ILogger<AiGateway> log)
{
    public async Task<bool> IsConfiguredAsync(AiRole role, CancellationToken ct = default) =>
        await settings.GetRoleAsync(role, ct) is not null;

    /// <summary>Asks the model and returns its reply as text.</summary>
    public async Task<string> CompleteAsync(
        AiRole role,
        string systemPrompt,
        IEnumerable<ChatMessage> conversation,
        AiCallContext context,
        int maxOutputTokens = 1024,
        bool jsonOutput = false,
        CancellationToken ct = default)
    {
        var config = await settings.GetRoleAsync(role, ct)
            ?? throw new AiUnavailableException($"No AI is set up for the {role} role yet. An admin can configure it under Admin → AI.");
        await budget.EnsureWithinBudgetAsync(context.HostUserId, ct);

        var client = factory.Create(config.Provider, config.Model);
        var options = BuildOptions(config, maxOutputTokens, jsonOutput);
        List<ChatMessage> messages = [new(ChatRole.System, systemPrompt), .. conversation];

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await client.GetStreamingResponseAsync(messages, options, ct).ToChatResponseAsync(ct);
            await RecordAsync(config, context, response.Usage, stopwatch, success: true, error: null, ct);

            var text = response.Text.Trim();
            if (text.Length == 0) throw new AiCallFailedException("The AI returned an empty answer. Please try again.");
            return text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AiException)
        {
            log.LogWarning(ex, "AI call failed for role {Role} using {Provider}/{Model}", role, config.Provider.Name, config.Model);
            await RecordAsync(config, context, null, stopwatch, success: false, error: ex.Message, ct);
            throw new AiCallFailedException($"The AI ({config.Provider.Name}) didn't respond properly. Please try again in a moment.", ex);
        }
    }

    /// <summary>Asks for JSON and parses it into <typeparamref name="T"/>, tolerating code fences and chatter around it.</summary>
    public async Task<T> CompleteJsonAsync<T>(
        AiRole role,
        string systemPrompt,
        IEnumerable<ChatMessage> conversation,
        AiCallContext context,
        int maxOutputTokens,
        CancellationToken ct = default)
    {
        var text = await CompleteAsync(role, systemPrompt, conversation, context, maxOutputTokens, jsonOutput: true, ct);
        return JsonExtraction.Parse<T>(text);
    }

    private static ChatOptions BuildOptions(AiRoleSettings config, int maxOutputTokens, bool jsonOutput)
    {
        var options = new ChatOptions
        {
            ModelId = config.Model,
            MaxOutputTokens = config.MaxOutputTokens is { } cap ? Math.Min(cap, maxOutputTokens) : maxOutputTokens,
        };

        // Current Claude models reject sampling settings like temperature, so only
        // send one when the admin set it and the provider accepts it.
        if (config.Temperature is { } t && config.Provider.Kind != AiProviderKind.Anthropic) options.Temperature = t;

        // OpenAI-style APIs (OpenAI, Gemini, Ollama) have a "JSON mode" that forces valid
        // JSON. For Claude the prompt asks for JSON and JsonExtraction copes with the rest.
        if (jsonOutput && config.Provider.Kind is AiProviderKind.OpenAI or AiProviderKind.Gemini or AiProviderKind.Ollama)
            options.ResponseFormat = ChatResponseFormat.Json;

        return options;
    }

    private async Task RecordAsync(AiRoleSettings config, AiCallContext context, UsageDetails? details, Stopwatch stopwatch, bool success, string? error, CancellationToken ct)
    {
        try
        {
            await usage.RecordAsync(new AiUsageRecord(
                clock.GetUtcNow(), config.Role, config.Provider.Kind, config.Provider.Name, config.Model,
                details?.InputTokenCount ?? 0, details?.OutputTokenCount ?? 0, stopwatch.ElapsedMilliseconds,
                success, error is null ? null : error[..Math.Min(error.Length, 500)], context), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Usage logging must never break the game itself.
            log.LogError(ex, "Failed to record AI usage");
        }
    }
}

public static class JsonExtraction
{
    /// <summary>
    /// Models sometimes wrap JSON in ```json fences or add a sentence before it.
    /// Take the outermost {...} block and parse that.
    /// </summary>
    public static T Parse<T>(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new AiCallFailedException("The AI didn't return JSON.");
        try
        {
            return JsonSerializer.Deserialize<T>(text[start..(end + 1)], GameJson.Options)
                ?? throw new AiCallFailedException("The AI returned empty JSON.");
        }
        catch (JsonException ex)
        {
            throw new AiCallFailedException($"The AI returned JSON that couldn't be read: {ex.Message}", ex);
        }
    }
}
