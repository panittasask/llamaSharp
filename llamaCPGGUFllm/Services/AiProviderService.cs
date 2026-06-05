using Microsoft.Extensions.Options;

namespace llamaCPGGUFllm.Services
{
    public sealed record AiProviderStatusResponse(
        string Provider,
        bool IsLlamaCpp,
        bool IsOpenAiCompatible,
        bool IsLmStudioLikely,
        string BaseUrl,
        string? ConfiguredModel
    );

    public class AiProviderService
    {
        private readonly IOptionsMonitor<LlmConfiguration> _configuration;
        private readonly object _lock = new();
        private string? _openAiModelOverride;

        public AiProviderService(IOptionsMonitor<LlmConfiguration> configuration)
        {
            _configuration = configuration;
        }

        public string GetEffectiveOpenAiModel()
        {
            var cfg = _configuration.CurrentValue;
            lock (_lock)
            {
                return string.IsNullOrWhiteSpace(_openAiModelOverride)
                    ? cfg.OpenAiModel
                    : _openAiModelOverride;
            }
        }

        public AiProviderStatusResponse SetOpenAiModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                throw new ArgumentException("Model is required.", nameof(model));
            }

            lock (_lock)
            {
                _openAiModelOverride = model.Trim();
            }

            return GetCurrentStatus();
        }

        public AiProviderStatusResponse GetCurrentStatus()
        {
            var cfg = _configuration.CurrentValue;
            var provider = (cfg.Provider ?? "llamacpp").Trim().ToLowerInvariant();
            var isLlamaCpp = provider == "llamacpp";
            var isOpenAiCompatible = provider == "openai";

            var baseUrl = isOpenAiCompatible ? cfg.OpenAiBaseUrl : cfg.BaseUrl;
            baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.Trim();

            var configuredModel = isOpenAiCompatible ? GetEffectiveOpenAiModel() : cfg.DefaultModelPath;
            configuredModel = string.IsNullOrWhiteSpace(configuredModel) ? null : configuredModel;

            var baseUrlLower = baseUrl.ToLowerInvariant();
            var isLmStudioLikely = isOpenAiCompatible
                && (baseUrlLower.Contains("127.0.0.1:1234") || baseUrlLower.Contains("localhost:1234") || baseUrlLower.Contains("lmstudio"));

            return new AiProviderStatusResponse(
                Provider: provider,
                IsLlamaCpp: isLlamaCpp,
                IsOpenAiCompatible: isOpenAiCompatible,
                IsLmStudioLikely: isLmStudioLikely,
                BaseUrl: baseUrl,
                ConfiguredModel: configuredModel
            );
        }
    }
}