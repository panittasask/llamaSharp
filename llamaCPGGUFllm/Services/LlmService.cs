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
        // Base URL ของ llama-server (llama.cpp) ที่เปิดไว้ เช่น http://127.0.0.1:8080
        public string BaseUrl { get; set; } = "http://127.0.0.1:8080";
        public int MaxTokens { get; set; } = 1024;
        public float Temperature { get; set; } = 0.7f;
        public float TopP { get; set; } = 0.9f;
        public string[] StopTokens { get; set; } = new[] { "<|im_end|>", "<|im_start|>" };
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
            if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_cfg.BaseUrl))
            {
                var baseUrl = _cfg.BaseUrl.EndsWith('/') ? _cfg.BaseUrl : _cfg.BaseUrl + "/";
                _http.BaseAddress = new Uri(baseUrl);
            }
            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        private object BuildPayload(string prompt, bool stream)
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

        public async IAsyncEnumerable<string> GenerateStreamAsync(string prompt, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "completion")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(BuildPayload(prompt, stream: true), JsonOpts),
                    Encoding.UTF8, "application/json")
            };

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line)) continue;

                // llama-server ส่ง SSE เป็น "data: { ... }"
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
                    if (root.TryGetProperty("content", out var contentEl))
                        chunk = contentEl.GetString();
                    if (root.TryGetProperty("stop", out var stopEl) && stopEl.ValueKind == JsonValueKind.True)
                        stop = true;
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
            using var req = new HttpRequestMessage(HttpMethod.Post, "completion")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(BuildPayload(prompt, stream: false), JsonOpts),
                    Encoding.UTF8, "application/json")
            };

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return doc.RootElement.TryGetProperty("content", out var contentEl)
                ? contentEl.GetString() ?? string.Empty
                : string.Empty;
        }
    }
}
