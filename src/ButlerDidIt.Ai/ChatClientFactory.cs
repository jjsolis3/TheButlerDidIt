using System.ClientModel;
using Anthropic;
using Anthropic.Helpers;
using ButlerDidIt.Ai.Fake;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

namespace ButlerDidIt.Ai;

public interface IChatClientFactory
{
    IChatClient Create(AiProviderSettings provider, string model);

    /// <param name="refusalFallbackModel">For Claude: a model that retries a request <paramref name="model"/> declined. Other providers ignore it.</param>
    IChatClient Create(AiProviderSettings provider, string model, string? refusalFallbackModel) => Create(provider, model);
}

/// <summary>
/// Turns a stored provider configuration into an <see cref="IChatClient"/>.
///
/// IChatClient (from Microsoft.Extensions.AI) is .NET's common interface for chat
/// models. Each vendor ships an adapter, so everything after this factory is
/// identical whichever AI you choose, and adding a provider means one new case here.
/// </summary>
public sealed class ChatClientFactory(bool allowFake) : IChatClientFactory
{
    public const string GeminiOpenAiEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/";
    public const string DefaultOllamaUrl = "http://localhost:11434";

    public IChatClient Create(AiProviderSettings provider, string model) => Create(provider, model, null);

    public IChatClient Create(AiProviderSettings provider, string model, string? refusalFallbackModel) => provider.Kind switch
    {
        // A declined request (stop_reason "refusal") is retried on the fallback model inside the same call. The SDK's
        // handler only acts on the beta messages API, so a role with a fallback talks to Claude through that.
        AiProviderKind.Anthropic when !string.IsNullOrWhiteSpace(refusalFallbackModel) =>
            CreateAnthropic(provider, new BetaRefusalFallbackHandler { Fallbacks = [new(refusalFallbackModel.Trim())] }).Beta.AsIChatClient(model),

        AiProviderKind.Anthropic => CreateAnthropic(provider).AsIChatClient(model),

        AiProviderKind.OpenAI => CreateOpenAi(provider.ApiKey, provider.BaseUrl).GetChatClient(model).AsIChatClient(),

        // Gemini speaks the OpenAI wire format at its own URL, so the OpenAI client works unchanged.
        AiProviderKind.Gemini => CreateOpenAi(provider.ApiKey, provider.BaseUrl ?? GeminiOpenAiEndpoint).GetChatClient(model).AsIChatClient(),

        AiProviderKind.Ollama => new OllamaApiClient(new Uri(provider.BaseUrl ?? DefaultOllamaUrl), model),

        AiProviderKind.Fake when allowFake => new FakeChatClient(),
        AiProviderKind.Fake => throw new AiUnavailableException("The fake AI provider is only allowed in tests."),

        _ => throw new AiUnavailableException($"Unknown AI provider kind {provider.Kind}."),
    };

    /// <summary>For tests: where Claude requests go instead of the network, so a test can read exactly what would be sent.</summary>
    internal HttpMessageHandler? AnthropicTransport { get; init; }

    private AnthropicClient CreateAnthropic(AiProviderSettings provider, DelegatingHandler? handler = null)
    {
        var apiKey = Require(provider.ApiKey, provider.Name);
        List<DelegatingHandler> handlers = handler is null ? [] : [handler];
        var baseUrl = string.IsNullOrWhiteSpace(provider.BaseUrl) ? null : provider.BaseUrl;
        return (AnthropicTransport, baseUrl) switch
        {
            (null, null) => new AnthropicClient { ApiKey = apiKey, Handlers = handlers },
            (null, { } url) => new AnthropicClient { ApiKey = apiKey, BaseUrl = url, Handlers = handlers },
            ({ } transport, _) => new AnthropicClient { ApiKey = apiKey, Handlers = handlers, HttpClient = new HttpClient(transport) },
        };
    }

    private static OpenAIClient CreateOpenAi(string? apiKey, string? baseUrl)
    {
        var options = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(baseUrl)) options.Endpoint = new Uri(baseUrl);
        // Some OpenAI-compatible servers don't need a key, but the client requires a value.
        return new OpenAIClient(new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) ? "none" : apiKey), options);
    }

    private static string Require(string? apiKey, string providerName) =>
        string.IsNullOrWhiteSpace(apiKey) ? throw new AiUnavailableException($"The AI provider '{providerName}' has no API key.") : apiKey;
}
