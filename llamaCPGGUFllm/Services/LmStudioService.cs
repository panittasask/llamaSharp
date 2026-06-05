using System.Text.Json;

namespace llamaCPGGUFllm.Services
{
    public sealed record LmStudioModelsResponse(
        bool ProviderAllowsQuery,
        string Provider,
        string BaseUrl,
        string? ConfiguredModel,
        int Count,
        string[] Models,
        string? Message
    );

    public class LmStudioService
    {
        private readonly HttpClient _httpClient;
        private readonly AiProviderService _providerService;

        public LmStudioService(HttpClient httpClient, AiProviderService providerService)
        {
            _httpClient = httpClient;
            _providerService = providerService;
        }

        public async Task<LmStudioModelsResponse> GetModelsAsync(CancellationToken cancellationToken = default)
        {
            var status = _providerService.GetCurrentStatus();
            if (!status.IsOpenAiCompatible)
            {
                return new LmStudioModelsResponse(
                    ProviderAllowsQuery: false,
                    Provider: status.Provider,
                    BaseUrl: status.BaseUrl,
                    ConfiguredModel: status.ConfiguredModel,
                    Count: 0,
                    Models: Array.Empty<string>(),
                    Message: "Current provider is not openai; LM Studio model listing is skipped."
                );
            }

            if (string.IsNullOrWhiteSpace(status.BaseUrl))
            {
                return new LmStudioModelsResponse(
                    ProviderAllowsQuery: false,
                    Provider: status.Provider,
                    BaseUrl: status.BaseUrl,
                    ConfiguredModel: status.ConfiguredModel,
                    Count: 0,
                    Models: Array.Empty<string>(),
                    Message: "OpenAI-compatible BaseUrl is empty."
                );
            }

            var baseUrl = status.BaseUrl.EndsWith('/') ? status.BaseUrl : status.BaseUrl + "/";
            var requestUri = new Uri(new Uri(baseUrl), "v1/models");

            try
            {
                using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return new LmStudioModelsResponse(
                        ProviderAllowsQuery: true,
                        Provider: status.Provider,
                        BaseUrl: status.BaseUrl,
                        ConfiguredModel: status.ConfiguredModel,
                        Count: 0,
                        Models: Array.Empty<string>(),
                        Message: $"Failed to query models: HTTP {(int)response.StatusCode}."
                    );
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<string>();
                if (json.RootElement.TryGetProperty("data", out var dataElement)
                    && dataElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var idElement)
                            && idElement.ValueKind == JsonValueKind.String)
                        {
                            var id = idElement.GetString();
                            if (!string.IsNullOrWhiteSpace(id))
                            {
                                models.Add(id);
                            }
                        }
                    }
                }

                var distinctModels = models
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new LmStudioModelsResponse(
                    ProviderAllowsQuery: true,
                    Provider: status.Provider,
                    BaseUrl: status.BaseUrl,
                    ConfiguredModel: status.ConfiguredModel,
                    Count: distinctModels.Length,
                    Models: distinctModels,
                    Message: distinctModels.Length == 0 ? "No models were returned by /v1/models." : null
                );
            }
            catch (Exception ex)
            {
                return new LmStudioModelsResponse(
                    ProviderAllowsQuery: true,
                    Provider: status.Provider,
                    BaseUrl: status.BaseUrl,
                    ConfiguredModel: status.ConfiguredModel,
                    Count: 0,
                    Models: Array.Empty<string>(),
                    Message: $"Failed to query LM Studio models: {ex.Message}"
                );
            }
        }
    }
}