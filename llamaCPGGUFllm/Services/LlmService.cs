using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace llamaCPGGUFllm.Services
{
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
        private readonly HttpClient _http;
        private readonly LlmConfiguration _cfg;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public LlmService(HttpClient http, IOptions<LlmConfiguration> config)
        {
            _cfg = config.Value;
            _http = http;
            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        private string Provider => _cfg.Provider?.Trim().ToLowerInvariant() ?? "llamacpp";

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
            var apiKey = _cfg.OpenAiApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            }
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("OpenAI API key not found. Set LlmConfiguration:OpenAiApiKey or OPENAI_API_KEY.");
            }

            var payload = new
            {
                model = _cfg.OpenAiModel,
                messages = new[] { new { role = "user", content = prompt } },
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
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            return req;
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

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    line = line.Substring(5).TrimStart();
                }
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
                            {
                                chunk = deltaContent.GetString();
                            }

                            if (choice0.TryGetProperty("finish_reason", out var finishReason)
                                && finishReason.ValueKind != JsonValueKind.Null)
                            {
                                stop = true;
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(chunk)) yield return chunk;
                if (stop) yield break;
            }
        }

        public async Task<string> GenerateFullTextAsync(string prompt, CancellationToken cancellationToken = default)
        {
            using var req = CreateRequest(prompt, stream: false);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (Provider == "llamacpp")
            {
                return doc.RootElement.TryGetProperty("content", out var contentEl)
                    ? contentEl.GetString() ?? string.Empty
                    : string.Empty;
            }

            if (doc.RootElement.TryGetProperty("choices", out var choicesEl)
                && choicesEl.ValueKind == JsonValueKind.Array
                && choicesEl.GetArrayLength() > 0)
            {
                var choice0 = choicesEl[0];
                if (choice0.TryGetProperty("message", out var msgEl)
                    && msgEl.TryGetProperty("content", out var msgContent)
                    && msgContent.ValueKind == JsonValueKind.String)
                {
                    return msgContent.GetString() ?? string.Empty;
                }
            }

            return string.Empty;
        }
    }
}
