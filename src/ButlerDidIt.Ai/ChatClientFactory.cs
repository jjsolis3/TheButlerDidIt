using System.ClientModel;
using Anthropic;
using ButlerDidIt.Ai.Fake;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

namespace ButlerDidIt.Ai;

public interface IChatClientFactory
{
    IChatClient Create(AiProviderSettings provider, string model);
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

    public IChatClient Create(AiProviderSettings provider, string model) => provider.Kind switch
    {
        AiProviderKind.Anthropic => CreateAnthropic(provider).AsIChatClient(model),

        AiProviderKind.OpenAI => CreateOpenAi(provider.ApiKey, provider.BaseUrl).GetChatClient(model).AsIChatClient(),

        // Gemini speaks the OpenAI wire format at its own URL, so the OpenAI client works unchanged.
        AiProviderKind.Gemini => CreateOpenAi(provider.ApiKey, provider.BaseUrl ?? GeminiOpenAiEndpoint).GetChatClient(model).AsIChatClient(),

        AiProviderKind.Ollama => new OllamaApiClient(new Uri(provider.BaseUrl ?? DefaultOllamaUrl), model),

        AiProviderKind.Fake when allowFake => new FakeChatClient(),
        AiProviderKind.Fake => throw new AiUnavailableException("The fake AI provider is only allowed in tests."),

        _ => throw new AiUnavailableException($"Unknown AI provider kind {provider.Kind}."),
    };

    private static AnthropicClient CreateAnthropic(AiProviderSettings provider)
    {
        var apiKey = Require(provider.ApiKey, provider.Name);
        return string.IsNullOrWhiteSpace(provider.BaseUrl)
            ? new AnthropicClient { ApiKey = apiKey }
            : new AnthropicClient { ApiKey = apiKey, BaseUrl = provider.BaseUrl };
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
