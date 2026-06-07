using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace llamaCPGGUFllm.Services
{
    public record ChatMessageItem(string Role, string Content);
    public sealed record ThinkingChatResult(
        string PlanSummary,
        string RefinedPrompt,
        string FinalResponse,
        bool UsedWebFallback,
        IReadOnlyList<WebSearchResult> SearchResults);

    public class LlmConfiguration
    {
        // Provider: "llamacpp" หรือ "openai"
        public string Provider { get; set; } = "llamacpp";

        // llama.cpp server
        public string BaseUrl { get; set; } = "http://127.0.0.1:8080";

        // OpenAI-compatible endpoint (OpenAI/Azure/OpenRouter ฯลฯ)
        public string OpenAiBaseUrl { get; set; } = "https://api.openai.com";
        public string OpenAiModel { get; set; } = "gpt-4o-mini";
        public string? OpenAiApiKey { get; set; }

        public int MaxTokens { get; set; } = 1024;
        public float Temperature { get; set; } = 0.7f;
        public float TopP { get; set; } = 0.9f;
        public string[] StopTokens { get; set; } = new[] { "<|im_end|>", "<|im_start|>" };

        // llama.cpp process control
        public string LlamaCppDirectory { get; set; } = "G:\\llama.cpp";
        public string LlamaServerExecutable { get; set; } = "llama-server.exe";
        public string ModelLocation { get; set; } = "G:\\Model\\";
        public string DefaultModelPath { get; set; } = "";
        public int GpuLayers { get; set; } = 99;
        public int ContextSize { get; set; } = 4096;
        public string ServerHost { get; set; } = "127.0.0.1";
        public int ServerPort { get; set; } = 8080;
        public string ExtraServerArgs { get; set; } = "";
    }

    public class LlmService
    {
        private const int MaxWebSearchAttempts = 2;
        private const string NoDirectSnippetText = "No direct snippet from API. Open result page for full search output.";

        private static readonly Regex ThinkBlockRegex = new(
            @"<think\b[^>]*>[\s\S]*?</think>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ThinkTagRegex = new(
            @"</?think\b[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly HttpClient _http;
        private readonly LlmConfiguration _cfg;
        private readonly AiProviderService _aiProviderService;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public LlmService(HttpClient http, IOptions<LlmConfiguration> config, AiProviderService aiProviderService)
        {
            _cfg = config.Value;
            _aiProviderService = aiProviderService;
            _http = http;
            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        private string Provider => _cfg.Provider?.Trim().ToLowerInvariant() ?? "llamacpp";

        private string ResolveApiKey()
        {
            var apiKey = _cfg.OpenAiApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
                apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                var baseUrlLower = (_cfg.OpenAiBaseUrl ?? string.Empty).ToLowerInvariant();
                if (baseUrlLower.Contains("127.0.0.1") || baseUrlLower.Contains("localhost"))
                    apiKey = "lm-studio";
            }
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("OpenAI API key not found. Set LlmConfiguration:OpenAiApiKey or OPENAI_API_KEY.");
            return apiKey;
        }

        private object BuildLlamaPayload(string prompt, bool stream)
        {
            var formatted = $"<|im_start|>user\n{prompt}<|im_end|>\n<|im_start|>assistant\n";
            return new
            {
                prompt = formatted,
                n_predict = _cfg.MaxTokens,
                temperature = _cfg.Temperature,
                top_p = _cfg.TopP,
                stop = _cfg.StopTokens,
                stream
            };
        }

        private static string EnsureTrailingSlash(string url)
        {
            return url.EndsWith('/') ? url : url + "/";
        }

        private HttpRequestMessage CreateLlamaRequest(string prompt, bool stream)
        {
            var payload = BuildLlamaPayload(prompt, stream);
            var baseUrl = EnsureTrailingSlash(_cfg.BaseUrl);
            return new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), "completion"))
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json")
            };
        }

        private HttpRequestMessage CreateOpenAiRequest(string prompt, bool stream)
        {
            var payload = new
            {
                model = _aiProviderService.GetEffectiveOpenAiModel(),
                messages = new[] { new { role = "system", content = "You are a helpful AI assistant." }, new { role = "user", content = prompt } },
                temperature = _cfg.Temperature,
                top_p = _cfg.TopP,
                max_tokens = _cfg.MaxTokens,
                stream
            };

            var baseUrl = EnsureTrailingSlash(_cfg.OpenAiBaseUrl);
            var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), "v1/chat/completions"))
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ResolveApiKey());
            return req;
        }

        // --- Multi-turn methods ---

        private string BuildMultiTurnChatMl(ChatMessageItem[] messages)
        {
            var sb = new StringBuilder();
            sb.Append("<|im_start|>system\nYou are a helpful AI assistant.<|im_end|>\n");
            foreach (var msg in messages)
            {
                var role = msg.Role.ToLowerInvariant() == "assistant" ? "assistant" : "user";
                sb.Append($"<|im_start|>{role}\n{msg.Content}<|im_end|>\n");
            }
            sb.Append("<|im_start|>assistant\n");
            return sb.ToString();
        }

        private HttpRequestMessage CreateLlamaChatRequest(ChatMessageItem[] messages, bool stream)
        {
            var prompt = BuildMultiTurnChatMl(messages);
            var payload = new
            {
                prompt,
                n_predict = _cfg.MaxTokens,
                temperature = _cfg.Temperature,
                top_p = _cfg.TopP,
                stop = _cfg.StopTokens,
                stream
            };
            var baseUrl = EnsureTrailingSlash(_cfg.BaseUrl);
            return new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), "completion"))
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json")
            };
        }

        private HttpRequestMessage CreateOpenAiChatRequest(ChatMessageItem[] messages, bool stream)
        {
            var msgList = messages.ToList();
            if (msgList.Count == 0 || msgList[0].Role.ToLowerInvariant() != "system")
                msgList.Insert(0, new ChatMessageItem("system", "You are a helpful AI assistant."));

            var payload = new
            {
                model = _aiProviderService.GetEffectiveOpenAiModel(),
                messages = msgList.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
                temperature = _cfg.Temperature,
                top_p = _cfg.TopP,
                max_tokens = _cfg.MaxTokens,
                stream
            };

            var baseUrl = EnsureTrailingSlash(_cfg.OpenAiBaseUrl);
            var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), "v1/chat/completions"))
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ResolveApiKey());
            return req;
        }

        private HttpRequestMessage CreateChatRequest(ChatMessageItem[] messages, bool stream)
        {
            return Provider switch
            {
                "llamacpp" => CreateLlamaChatRequest(messages, stream),
                "openai" => CreateOpenAiChatRequest(messages, stream),
                _ => throw new InvalidOperationException($"Unsupported provider '{_cfg.Provider}'.")
            };
        }

        private HttpRequestMessage CreateRequest(string prompt, bool stream)
        {
            return Provider switch
            {
                "llamacpp" => CreateLlamaRequest(prompt, stream),
                "openai" => CreateOpenAiRequest(prompt, stream),
                _ => throw new InvalidOperationException($"Unsupported provider '{_cfg.Provider}'. Use 'llamacpp' or 'openai'.")
            };
        }

        public async IAsyncEnumerable<string> GenerateStreamAsync(string prompt, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var req = CreateRequest(prompt, stream: true);
            await foreach (var chunk in ReadStreamResponseAsync(req, cancellationToken))
                yield return chunk;
        }

        public async Task<string> GenerateFullTextAsync(string prompt, CancellationToken cancellationToken = default)
        {
            using var req = CreateRequest(prompt, stream: false);
            return await ReadFullResponseAsync(req, cancellationToken);
        }

        public async IAsyncEnumerable<string> GenerateStreamFromMessagesAsync(ChatMessageItem[] messages, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var req = CreateChatRequest(messages, stream: true);
            await foreach (var chunk in ReadStreamResponseAsync(req, cancellationToken))
                yield return chunk;
        }

        public async Task<string> GenerateFullTextFromMessagesAsync(ChatMessageItem[] messages, CancellationToken cancellationToken = default)
        {
            var response = await GenerateRawFullTextFromMessagesAsync(messages, cancellationToken);
            if (!ShouldUseWebFallback(response))
            {
                return response;
            }

            var query = ExtractLatestUserMessage(messages);
            if (string.IsNullOrWhiteSpace(query))
            {
                return response;
            }

            var fallback = await ResolveWebFallbackAsync(query, cancellationToken);
            return fallback.Answer;
        }

        private async Task<string> GenerateRawFullTextFromMessagesAsync(ChatMessageItem[] messages, CancellationToken cancellationToken)
        {
            using var req = CreateChatRequest(messages, stream: false);
            return await ReadFullResponseAsync(req, cancellationToken);
        }

        public async Task<ThinkingChatResult> GenerateThinkingResponseWithFallbackAsync(
            ChatMessageItem[] messages,
            CancellationToken cancellationToken = default)
        {
            if (messages == null || messages.Length == 0)
            {
                throw new ArgumentException("Messages are required.", nameof(messages));
            }

            var userPrompt = messages
                .LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                ?.Content
                ?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(userPrompt))
            {
                userPrompt = messages.Last().Content?.Trim() ?? string.Empty;
            }

            var planningPrompt = BuildThinkingPlannerPrompt(messages, userPrompt);
            var plannerOutput = await GenerateFullTextAsync(planningPrompt, cancellationToken);
            var refinedPrompt = ExtractRefinedPrompt(plannerOutput, userPrompt);
            var planSummary = ExtractPlanSummary(plannerOutput);
            var shouldSearchWeb = ExtractWebSearchDecision(plannerOutput);
            var plannedSearchQuery = ExtractWebSearchQuery(plannerOutput, refinedPrompt, userPrompt);

            IReadOnlyList<WebSearchResult> preSearchResults = Array.Empty<WebSearchResult>();
            if (shouldSearchWeb)
            {
                preSearchResults = await SearchDuckDuckGoAsync(plannedSearchQuery, 5, cancellationToken);
            }

            var refinedMessages = BuildRefinedMessages(messages, refinedPrompt, planSummary, preSearchResults);
            var modelResponse = await GenerateRawFullTextFromMessagesAsync(refinedMessages, cancellationToken);

            if (!ShouldUseWebFallback(modelResponse))
            {
                return new ThinkingChatResult(
                    PlanSummary: planSummary,
                    RefinedPrompt: refinedPrompt,
                    FinalResponse: modelResponse,
                    UsedWebFallback: false,
                    SearchResults: preSearchResults);
            }

            var query = shouldSearchWeb
                ? plannedSearchQuery
                : (string.IsNullOrWhiteSpace(refinedPrompt) ? userPrompt : refinedPrompt);
            var fallback = await ResolveWebFallbackAsync(query, cancellationToken);

            return new ThinkingChatResult(
                PlanSummary: planSummary,
                RefinedPrompt: refinedPrompt,
                FinalResponse: fallback.Answer,
                UsedWebFallback: true,
                SearchResults: fallback.Results);
        }

        private string BuildThinkingPlannerPrompt(ChatMessageItem[] messages, string latestUserPrompt)
        {
            var recent = messages
                .TakeLast(8)
                .Select((m, i) => $"{i + 1}. {m.Role}: {m.Content}")
                .ToArray();

            var recentBlock = recent.Length == 0
                ? "(none)"
                : string.Join("\n", recent);

            return $"""
You are a planning assistant.
Goal: refine the user's intent into a clear execution prompt.

Return EXACTLY this format:
PLAN:
- step 1
- step 2

ANSWER ALWAY HAVE
WEB_SEARCH_DECISION: yes/no
SEARCH_QUERY: <query if yes, else empty>

REFINED_PROMPT:
<single refined prompt paragraph>

Recent conversation:
{recentBlock}

Latest user prompt:
{latestUserPrompt}
""";
        }

        private string ExtractRefinedPrompt(string plannerOutput, string fallbackPrompt)
        {
            if (string.IsNullOrWhiteSpace(plannerOutput))
            {
                return fallbackPrompt;
            }

            const string refinedMarker = "REFINED_PROMPT:";
            var index = plannerOutput.IndexOf(refinedMarker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return fallbackPrompt;
            }

            var refined = plannerOutput[(index + refinedMarker.Length)..].Trim();
            return string.IsNullOrWhiteSpace(refined) ? fallbackPrompt : refined;
        }

        private bool ExtractWebSearchDecision(string plannerOutput)
        {
            if (string.IsNullOrWhiteSpace(plannerOutput))
            {
                return false;
            }

            var line = plannerOutput
                .Split('\n')
                .FirstOrDefault(x => x.TrimStart().StartsWith("WEB_SEARCH_DECISION:", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var value = line[(line.IndexOf(':') + 1)..].Trim().ToLowerInvariant();
            return value is "yes" or "true" or "search";
        }

        private string ExtractWebSearchQuery(string plannerOutput, string refinedPrompt, string userPrompt)
        {
            if (!string.IsNullOrWhiteSpace(plannerOutput))
            {
                var line = plannerOutput
                    .Split('\n')
                    .FirstOrDefault(x => x.TrimStart().StartsWith("SEARCH_QUERY:", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(line))
                {
                    var value = line[(line.IndexOf(':') + 1)..].Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(refinedPrompt))
            {
                return refinedPrompt;
            }

            return userPrompt;
        }

        private string ExtractPlanSummary(string plannerOutput)
        {
            if (string.IsNullOrWhiteSpace(plannerOutput))
            {
                return "No plan generated.";
            }

            const string planMarker = "PLAN:";
            const string refinedMarker = "REFINED_PROMPT:";
            var start = plannerOutput.IndexOf(planMarker, StringComparison.OrdinalIgnoreCase);
            var end = plannerOutput.IndexOf(refinedMarker, StringComparison.OrdinalIgnoreCase);

            if (start < 0)
            {
                return plannerOutput.Trim();
            }

            if (end > start)
            {
                return plannerOutput[(start + planMarker.Length)..end].Trim();
            }

            return plannerOutput[(start + planMarker.Length)..].Trim();
        }

        private ChatMessageItem[] BuildRefinedMessages(
            ChatMessageItem[] original,
            string refinedPrompt,
            string planSummary,
            IReadOnlyList<WebSearchResult> webResults)
        {
            var list = original.ToList();
            var lastUserIndex = list.FindLastIndex(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));

            var webContext = string.Empty;
            if (webResults.Count > 0)
            {
                var webLines = webResults
                    .Take(5)
                    .Select((item, index) =>
                        $"{index + 1}. {item.Title}\nURL: {item.Url}\nSnippet: {item.Snippet}");
                webContext = "\n\nWeb context:\n" + string.Join("\n\n", webLines);
            }

            var combinedPrompt =
                $"Use the following planning context internally only. " +
                "Do NOT output planning steps, PLAN:, REFINED_PROMPT:, WEB_SEARCH_DECISION:, SEARCH_QUERY:, or chain-of-thought. " +
                "Return only the final user-facing answer.\n\n" +
                $"Plan before execution:\n{planSummary}\n\nRefined request:\n{refinedPrompt}{webContext}";

            if (lastUserIndex >= 0)
            {
                list[lastUserIndex] = new ChatMessageItem("user", combinedPrompt);
            }
            else
            {
                list.Add(new ChatMessageItem("user", combinedPrompt));
            }

            return list.ToArray();
        }

        private bool ShouldUseWebFallback(string response)
        {
            return
                LooksLikeOutOfData(response) ||
                LooksLikeGuessyResponse(response) ||
                LooksLikePlannerLeak(response);
        }

        private bool LooksLikeOutOfData(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return true;
            }

            var text = response.ToLowerInvariant();
            var markers = new[]
            {
                "i don't know",
                "i do not know",
                "not enough information",
                "insufficient information",
                "cannot answer",
                "can't answer",
                "out of data",
                "no data available",
                "ไม่มีข้อมูล",
                "ข้อมูลไม่เพียงพอ",
                "ไม่สามารถตอบ",
                "ไม่ทราบ",
            };

            return markers.Any(marker => text.Contains(marker, StringComparison.Ordinal));
        }

        private bool LooksLikeGuessyResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return true;
            }

            var text = response.ToLowerInvariant();
            var markers = new[]
            {
                "maybe",
                "likely",
                "possibly",
                "probably",
                "might be",
                "not sure",
                "i think",
                "cannot access real-time",
                "can't access real-time",
                "unable to access real-time",
                "check your device clock",
                "please check your device",
                "time.is",
                "น่าจะ",
                "อาจจะ",
                "ประมาณ",
                "ไม่แน่ใจ",
                "คาดว่า",
                "ไม่สามารถเข้าถึงข้อมูล",
                "ไม่สามารถเข้าถึงข้อมูลเวลา",
                "เรียลไทม์",
                "ตรวจสอบจากนาฬิกา",
                "นาฬิกาบนมือถือ",
                "นาฬิกาบนคอมพิวเตอร์",
                "ขออภัยในความไม่สะดวก",
            };

            return markers.Any(marker => text.Contains(marker, StringComparison.Ordinal));
        }

        private bool LooksLikePlannerLeak(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            var text = response.ToLowerInvariant();
            var markers = new[]
            {
                "plan:",
                "refined_prompt:",
                "web_search_decision:",
                "search_query:",
                "plan before execution",
                "step 1",
                "step 2",
            };

            var hitCount = markers.Count(marker => text.Contains(marker, StringComparison.Ordinal));
            return hitCount >= 2;
        }

        private async Task<(string Answer, IReadOnlyList<WebSearchResult> Results)> ResolveWebFallbackAsync(
            string originalQuery,
            CancellationToken cancellationToken)
        {
            var query = originalQuery.Trim();
            IReadOnlyList<WebSearchResult> lastResults = Array.Empty<WebSearchResult>();

            for (var attempt = 1; attempt <= MaxWebSearchAttempts; attempt++)
            {
                lastResults = await SearchDuckDuckGoAsync(query, 6, cancellationToken);
                if (HasInformativeSearchResults(lastResults))
                {
                    var answer = await SynthesizeAnswerFromSearchAsync(originalQuery, lastResults, cancellationToken);
                    if (!ShouldUseWebFallback(answer) && !LooksLikeRawSearchDump(answer))
                    {
                        return (answer, lastResults);
                    }
                }

                query = BuildNextSearchQuery(originalQuery, attempt);
            }

            if (LooksLikeDateTimeQuery(originalQuery))
            {
                return (BuildCurrentDateTimeAnswer(), lastResults);
            }

            if (lastResults.Count > 0)
            {
                var fallbackAnswer = await SynthesizeAnswerFromSearchAsync(originalQuery, lastResults, cancellationToken);
                return (fallbackAnswer, lastResults);
            }

            return ("ยังหาข้อมูลที่เชื่อถือได้จากเว็บไม่สำเร็จ กรุณาลองถามใหม่โดยเพิ่มรายละเอียด", Array.Empty<WebSearchResult>());
        }

        private bool HasInformativeSearchResults(IReadOnlyList<WebSearchResult> results)
        {
            if (results.Count == 0)
            {
                return false;
            }

            return results.Any(result =>
                !string.IsNullOrWhiteSpace(result.Url)
                && !result.Url.Contains("duckduckgo.com/?q=", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(result.Snippet?.Trim(), NoDirectSnippetText, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(result.Snippet));
        }

        private string BuildNextSearchQuery(string originalQuery, int attempt)
        {
            return attempt switch
            {
                1 => $"latest verified facts {originalQuery}",
                _ => $"{originalQuery} official source",
            };
        }

        private async Task<string> SynthesizeAnswerFromSearchAsync(
            string originalQuery,
            IReadOnlyList<WebSearchResult> results,
            CancellationToken cancellationToken)
        {
            var context = string.Join(
                "\n\n",
                results.Take(6).Select((item, index) =>
                    $"[{index + 1}] {item.Title}\nURL: {item.Url}\nSnippet: {item.Snippet}"));

            var prompt = $"""
You are given web search evidence.
Answer the user's question using only the evidence below.

Rules:
1) Return a direct final answer in Thai.
2) Do NOT output PLAN, REFINED_PROMPT, WEB_SEARCH_DECISION, SEARCH_QUERY.
3) Do NOT dump raw bullet list of all search results unless user asked.
4) If evidence is weak, give the best concise answer and include source URL(s) used.

User question:
{originalQuery}

Evidence:
{context}
""";

            return await GenerateFullTextAsync(prompt, cancellationToken);
        }

        private bool LooksLikeRawSearchDump(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            var text = response.ToLowerInvariant();
            return text.Contains("i could not confidently answer from model data", StringComparison.Ordinal)
                || text.Contains("search duckduckgo for:", StringComparison.Ordinal)
                || text.Contains("no direct snippet from api", StringComparison.Ordinal);
        }

        private bool LooksLikeDateTimeQuery(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var normalized = text.ToLowerInvariant();
            var markers = new[]
            {
                "วันนี้วันอะไร",
                "เวลาอะไร",
                "วันที่เท่าไหร่",
                "what day is it",
                "what time is it",
                "current time",
                "current date",
                "timezone",
            };

            return markers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
        }

        private string BuildCurrentDateTimeAnswer()
        {
            var tz = ResolveBangkokTimeZone();
            var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
            return $"เวลาปัจจุบันตามระบบเซิร์ฟเวอร์คือ {now:yyyy-MM-dd HH:mm:ss} (Asia/Bangkok, GMT+7)";
        }

        private static TimeZoneInfo ResolveBangkokTimeZone()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
            }
            catch
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok");
            }
        }

        private async Task<IReadOnlyList<WebSearchResult>> SearchDuckDuckGoAsync(
            string query,
            int limit,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<WebSearchResult>();
            }

            limit = Math.Clamp(limit, 1, 10);
            var results = new List<WebSearchResult>();
            var requestUrl = $"https://api.duckduckgo.com/?q={Uri.EscapeDataString(query)}&format=json&no_html=1&skip_disambig=1";
            string payload;
            try
            {
                payload = await _http.GetStringAsync(requestUrl, cancellationToken);
            }
            catch
            {
                return CreateDuckDuckGoFallbackResults(query, limit, "DuckDuckGo request failed.");
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(payload);
            }
            catch (JsonException)
            {
                return CreateDuckDuckGoFallbackResults(query, limit, "DuckDuckGo returned a non-JSON payload.");
            }

            using (doc)
            {
                if (doc.RootElement.TryGetProperty("AbstractURL", out var abstractUrlElement)
                    && doc.RootElement.TryGetProperty("AbstractText", out var abstractTextElement))
                {
                    var abstractUrl = abstractUrlElement.GetString() ?? string.Empty;
                    var abstractText = abstractTextElement.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(abstractUrl) && !string.IsNullOrWhiteSpace(abstractText))
                    {
                        results.Add(new WebSearchResult
                        {
                            Title = doc.RootElement.TryGetProperty("Heading", out var headingElement)
                                ? headingElement.GetString() ?? abstractUrl
                                : abstractUrl,
                            Url = abstractUrl,
                            Snippet = abstractText,
                            Source = "duckduckgo",
                        });
                    }
                }

                if (doc.RootElement.TryGetProperty("RelatedTopics", out var relatedTopics)
                    && relatedTopics.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in relatedTopics.EnumerateArray())
                    {
                        if (results.Count >= limit)
                        {
                            break;
                        }

                        if (item.TryGetProperty("FirstURL", out var firstUrlElement)
                            && item.TryGetProperty("Text", out var textElement))
                        {
                            var url = firstUrlElement.GetString() ?? string.Empty;
                            var text = textElement.GetString() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(text))
                            {
                                results.Add(new WebSearchResult
                                {
                                    Title = text.Split('-', 2)[0].Trim(),
                                    Url = url,
                                    Snippet = text,
                                    Source = "duckduckgo",
                                });
                            }
                        }
                    }
                }
            }

            if (results.Count == 0)
            {
                return CreateDuckDuckGoFallbackResults(query, limit, NoDirectSnippetText);
            }

            return results.Take(limit).ToList();
        }

        private List<WebSearchResult> CreateDuckDuckGoFallbackResults(string query, int limit, string snippet)
        {
            return new List<WebSearchResult>
            {
                new()
                {
                    Title = $"Search DuckDuckGo for: {query}",
                    Url = $"https://duckduckgo.com/?q={Uri.EscapeDataString(query)}",
                    Snippet = snippet,
                    Source = "duckduckgo",
                },
            }.Take(Math.Max(1, limit)).ToList();
        }

        private string BuildWebFallbackResponse(string query, IReadOnlyList<WebSearchResult> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine("I could not confidently answer from model data, so I searched the web (DuckDuckGo):");
            sb.AppendLine($"Query: {query}");
            sb.AppendLine();

            foreach (var item in results.Take(5))
            {
                sb.AppendLine($"- {item.Title}");
                sb.AppendLine($"  {item.Url}");
                sb.AppendLine($"  {item.Snippet}");
                sb.AppendLine();
            }

            return sb.ToString().Trim();
        }

        private static string ExtractLatestUserMessage(ChatMessageItem[] messages)
        {
            if (messages == null || messages.Length == 0)
            {
                return string.Empty;
            }

            return messages
                .LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                ?.Content
                ?.Trim() ?? string.Empty;
        }

        private static string StripThinkBlocks(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var withoutBlocks = ThinkBlockRegex.Replace(text, string.Empty);
            return ThinkTagRegex.Replace(withoutBlocks, string.Empty);
        }

        private sealed class ThinkTagStreamFilter
        {
            private const int OpenTagMaxTail = 6; // "<think" length
            private const int CloseTagMaxTail = 7; // "</think" length

            private readonly StringBuilder _buffer = new();
            private bool _insideThink;

            public string Process(string input)
            {
                if (string.IsNullOrEmpty(input))
                {
                    return string.Empty;
                }

                _buffer.Append(input);
                var output = new StringBuilder();

                while (true)
                {
                    var current = _buffer.ToString();
                    if (_insideThink)
                    {
                        var closeIndex = current.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                        if (closeIndex < 0)
                        {
                            if (current.Length > CloseTagMaxTail)
                            {
                                _buffer.Clear();
                                _buffer.Append(current[^CloseTagMaxTail..]);
                            }

                            break;
                        }

                        var next = current[(closeIndex + 8)..];
                        _insideThink = false;
                        _buffer.Clear();
                        _buffer.Append(next);
                        continue;
                    }

                    var openIndex = current.IndexOf("<think", StringComparison.OrdinalIgnoreCase);
                    if (openIndex < 0)
                    {
                        if (current.Length > OpenTagMaxTail)
                        {
                            var safe = current[..(current.Length - OpenTagMaxTail)];
                            output.Append(safe);
                            _buffer.Clear();
                            _buffer.Append(current[(current.Length - OpenTagMaxTail)..]);
                        }

                        break;
                    }

                    if (openIndex > 0)
                    {
                        output.Append(current[..openIndex]);
                    }

                    var gtIndex = current.IndexOf('>', openIndex);
                    if (gtIndex < 0)
                    {
                        _buffer.Clear();
                        _buffer.Append(current[openIndex..]);
                        break;
                    }

                    _insideThink = true;
                    _buffer.Clear();
                    _buffer.Append(current[(gtIndex + 1)..]);
                }

                return ThinkTagRegex.Replace(output.ToString(), string.Empty);
            }

            public string Flush()
            {
                if (_insideThink)
                {
                    _buffer.Clear();
                    return string.Empty;
                }

                var remaining = _buffer.ToString();
                _buffer.Clear();
                return StripThinkBlocks(remaining);
            }
        }

        // --- Shared response readers ---

        private async IAsyncEnumerable<string> ReadStreamResponseAsync(HttpRequestMessage req, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            var thinkFilter = new ThinkTagStreamFilter();

            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("data:", StringComparison.Ordinal))
                    line = line.Substring(5).TrimStart();
                if (line == "[DONE]") yield break;

                string? chunk = null;
                bool stop = false;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (Provider == "llamacpp")
                    {
                        if (root.TryGetProperty("content", out var contentEl))
                            chunk = contentEl.GetString();
                        if (root.TryGetProperty("stop", out var stopEl) && stopEl.ValueKind == JsonValueKind.True)
                            stop = true;
                    }
                    else
                    {
                        if (root.TryGetProperty("choices", out var choicesEl) && choicesEl.ValueKind == JsonValueKind.Array && choicesEl.GetArrayLength() > 0)
                        {
                            var choice0 = choicesEl[0];
                            if (choice0.TryGetProperty("delta", out var deltaEl)
                                && deltaEl.TryGetProperty("content", out var deltaContent)
                                && deltaContent.ValueKind == JsonValueKind.String)
                                chunk = deltaContent.GetString();

                            if (choice0.TryGetProperty("finish_reason", out var finishReason)
                                && finishReason.ValueKind != JsonValueKind.Null)
                                stop = true;
                        }
                    }
                }
                catch (JsonException) { continue; }

                if (!string.IsNullOrEmpty(chunk))
                {
                    var sanitized = thinkFilter.Process(chunk);
                    if (!string.IsNullOrEmpty(sanitized))
                    {
                        yield return sanitized;
                    }
                }

                if (stop) break;
            }

            var tail = thinkFilter.Flush();
            if (!string.IsNullOrEmpty(tail))
            {
                yield return tail;
            }
        }

        private async Task<string> ReadFullResponseAsync(HttpRequestMessage req, CancellationToken cancellationToken)
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (Provider == "llamacpp")
                return doc.RootElement.TryGetProperty("content", out var contentEl)
                    ? StripThinkBlocks(contentEl.GetString() ?? string.Empty)
                    : string.Empty;

            if (doc.RootElement.TryGetProperty("choices", out var choicesEl)
                && choicesEl.ValueKind == JsonValueKind.Array
                && choicesEl.GetArrayLength() > 0)
            {
                var choice0 = choicesEl[0];
                if (choice0.TryGetProperty("message", out var msgEl)
                    && msgEl.TryGetProperty("content", out var msgContent)
                    && msgContent.ValueKind == JsonValueKind.String)
                    return StripThinkBlocks(msgContent.GetString() ?? string.Empty);
            }

            return string.Empty;
        }
    }
}
