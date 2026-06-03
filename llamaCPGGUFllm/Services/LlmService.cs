using System.Runtime.CompilerServices;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Options;

namespace llamaCPGGUFllm.Services
{
    public class LlmConfiguration
    {
        public string ModelPath { get; set; } = string.Empty;
        public uint ContextSize { get; set; } = 4096;
        public int GpuLayerCount { get; set; } = 35;
    }
    public class LlmService : IDisposable
    {
        string filePath = "G:\\C# llama\\Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf";
        private readonly LLamaWeights _weights;
        private readonly LLamaContext _context;
        private readonly StatelessExecutor _executor;
        public LlmService(IOptions<LlmConfiguration> config)
        {
            var parameters = new ModelParams(config.Value.ModelPath)
            {
                ContextSize = config.Value.ContextSize,
                GpuLayerCount = config.Value.GpuLayerCount
            };

            // โหลดโมเดลเข้า RAM/VRAM แค่ครั้งเดียวตอนเริ่มแอป
            _weights = LLamaWeights.LoadFromFile(parameters);
            _context = _weights.CreateContext(parameters);
            _executor = new StatelessExecutor(_weights, parameters);
        }
        public async IAsyncEnumerable<string> GenerateStreamAsync(string prompt, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // 1. จัด Format Prompt ให้เป็นสไตล์ Qwen (ChatML) เพื่อให้โมเดลตอบได้ตรงประเด็น
            string formattedPrompt = $"<|im_start|>user\n{prompt}<|im_end|>\n<|im_start|>assistant\n";

            // 2. ตั้งค่าพารามิเตอร์ (รูปแบบโค้ดที่ถูกต้องของ V 0.27.0)
            var inferenceParams = new InferenceParams()
            {
                MaxTokens = 1024,
                AntiPrompts = ["<|im_end|>", "<|im_start|>"], // สั่งให้หยุดเมื่อเจอ Tag เหล่านี้
                SamplingPipeline = new DefaultSamplingPipeline()
                {
                    Temperature = 0.7f,
                    TopP = 0.9f
                }
            };

            // 3. สั่งรันและทยอยส่งคำตอบ (yield return) กลับไปทันทีที่ได้คำมา
            await foreach (var text in _executor.InferAsync(formattedPrompt, inferenceParams, cancellationToken))
            {
                yield return text;
            }

        }
        public async Task<string> GenerateFullTextAsync(string prompt, CancellationToken cancellationToken = default)
        {
            string formattedPrompt = $"<|im_start|>user\n{prompt}<|im_end|>\n<|im_start|>assistant\n";

            var inferenceParams = new InferenceParams()
            {
                MaxTokens = 1024,
                AntiPrompts = ["<|im_end|>", "<|im_start|>"],
                SamplingPipeline = new DefaultSamplingPipeline()
                {
                    Temperature = 0.7f,
                    TopP = 0.9f
                }
            };

            var fullResponse = new System.Text.StringBuilder();

            // วนลูปเก็บคำเอาไว้จนกว่าจะหมด โดยยังไม่พ่นออกไปหา Client
            await foreach (var text in _executor.InferAsync(formattedPrompt, inferenceParams, cancellationToken))
            {
                fullResponse.Append(text);
            }

            return fullResponse.ToString();
        }
        public void Dispose()
        {
            _context?.Dispose();
            _weights?.Dispose();
        }

    }

}
