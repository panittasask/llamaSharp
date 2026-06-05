using System.Diagnostics;
using llamaCPGGUFllm.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace llamaCPGGUFllm.Controllers
{
    public record PromptRequest(string Prompt);
    public record ChatMessagesRequest(ChatMessageItem[] Messages);
    public record StartServerRequest(string? ModelPath);
    public record SwitchModelRequest(string ModelPath);
    public record SetOpenAiModelRequest(string Model);

    public sealed record ServerConfigResponse(int ContextSize, int MaxTokens);

    [ApiController]
    [Route("api/[controller]")]
    public class LlmController : ControllerBase
    {
        private readonly LlmService _llmService;
        private readonly LlamaServerManager _serverManager;
        private readonly AiProviderService _aiProviderService;
        private readonly LmStudioService _lmStudioService;
        private readonly LlmConfiguration _configuration;

        // ฉีด LlmService เข้ามาใช้งานผ่าน DI
        public LlmController(
            LlmService llmService,
            LlamaServerManager serverManager,
            AiProviderService aiProviderService,
            LmStudioService lmStudioService,
            IOptions<LlmConfiguration> configuration)
        {
            _llmService = llmService;
            _serverManager = serverManager;
            _aiProviderService = aiProviderService;
            _lmStudioService = lmStudioService;
            _configuration = configuration.Value;
        }

        [HttpGet("provider/status")]
        public IActionResult GetProviderStatus()
        {
            return Ok(_aiProviderService.GetCurrentStatus());
        }

        [HttpGet("provider/lmstudio/models")]
        public async Task<IActionResult> GetLmStudioModels(CancellationToken ct)
        {
            var result = await _lmStudioService.GetModelsAsync(ct);
            return Ok(result);
        }

        [HttpPost("provider/openai/model")]
        public IActionResult SetOpenAiModel([FromBody] SetOpenAiModelRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Model))
            {
                return BadRequest("Model is required");
            }

            var result = _aiProviderService.SetOpenAiModel(request.Model);
            return Ok(result);
        }

        [HttpGet("server/status")]
        public IActionResult GetServerStatus()
        {
            return Ok(_serverManager.GetStatus());
        }

        [HttpGet("server/models")]
        public IActionResult GetAvailableModels()
        {
            var models = _serverManager.ListModels();
            return Ok(new { count = models.Length, models });
        }

        [HttpGet("server/config")]
        public IActionResult GetServerConfig()
        {
            return Ok(new ServerConfigResponse(_configuration.ContextSize, _configuration.MaxTokens));
        }

        [HttpPost("server/start")]
        public IActionResult StartServer([FromBody] StartServerRequest? request)
        {
            var result = _serverManager.Start(request?.ModelPath);
            return result.IsRunning ? Ok(result) : BadRequest(result);
        }

        [HttpPost("server/stop")]
        public IActionResult StopServer()
        {
            return Ok(_serverManager.Stop());
        }

        [HttpPost("server/switch-model")]
        public IActionResult SwitchModel([FromBody] SwitchModelRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.ModelPath))
            {
                return BadRequest("ModelPath is required");
            }

            var result = _serverManager.SwitchModel(request.ModelPath);
            return result.IsRunning ? Ok(result) : BadRequest(result);
        }

        [HttpPost("stream")]
        public async Task GetStreamAsync([FromBody] PromptRequest request, CancellationToken ct)
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.Append("Cache-Control", "no-cache");
            Response.Headers.Append("Connection", "keep-alive");
            try
            {
                await foreach (var chunk in _llmService.GenerateStreamAsync(request.Prompt, ct))
                {
                    await Response.WriteAsync($"data: {chunk.Replace("\n", "\\n").Replace("\r", "")}\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                }
                await Response.WriteAsync("data: [DONE]\n\n", ct);
            }
            catch (OperationCanceledException) { }
        }

        [HttpPost("chat/stream")]
        public async Task GetChatStreamAsync([FromBody] ChatMessagesRequest request, CancellationToken ct)
        {
            if (request.Messages == null || request.Messages.Length == 0)
            {
                Response.StatusCode = 400;
                return;
            }

            Response.ContentType = "text/event-stream";
            Response.Headers.Append("Cache-Control", "no-cache");
            Response.Headers.Append("Connection", "keep-alive");
            try
            {
                await foreach (var chunk in _llmService.GenerateStreamFromMessagesAsync(request.Messages, ct))
                {
                    await Response.WriteAsync($"data: {chunk.Replace("\n", "\\n").Replace("\r", "")}\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                }
                await Response.WriteAsync("data: [DONE]\n\n", ct);
            }
            catch (OperationCanceledException) { }
        }

        [HttpPost("chat/normal")]
        public async Task<IActionResult> PostChatNormalAsync([FromBody] ChatMessagesRequest request, CancellationToken ct)
        {
            if (request.Messages == null || request.Messages.Length == 0)
                return BadRequest("Messages array is required.");

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var responseText = await _llmService.GenerateFullTextFromMessagesAsync(request.Messages, ct);
                stopwatch.Stop();
                var lastUserPrompt = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
                await WriteLogAsync(lastUserPrompt, responseText, stopwatch.ElapsedMilliseconds);
                return Ok(new
                {
                    Response = responseText,
                    TimeElapsedMs = stopwatch.ElapsedMilliseconds,
                    TimeElapsedSec = stopwatch.ElapsedMilliseconds / 1000.0
                });
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        [HttpPost("normal")]
        public async Task<IActionResult> PostNormalAsync([FromBody] PromptRequest request, CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                string responseText = await _llmService.GenerateFullTextAsync(request.Prompt, ct);
                stopwatch.Stop();
                await WriteLogAsync(request.Prompt, responseText, stopwatch.ElapsedMilliseconds);
                return Ok(new
                {
                    Response = responseText,
                    TimeElapsedMs = stopwatch.ElapsedMilliseconds,
                    TimeElapsedSec = stopwatch.ElapsedMilliseconds / 1000.0
                });
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        // ฟังก์ชันสำหรับเขียน Log ลงไฟล์ txt
        private async Task WriteLogAsync(string prompt, string response, long elapsedMs)
        {
            try
            {
                // กำหนด Path ให้ไปอยู่ที่โฟลเดอร์ logs/ แยกไฟล์ตามวัน
                string logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "logs");
                string logFilePath = Path.Combine(logDirectory, $"log_{DateTime.Now:yyyyMMdd}.txt");

                // ตรวจสอบว่ามีโฟลเดอร์หรือยัง ถ้าไม่มีให้สร้าง
                if (!Directory.Exists(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }

                // เตรียมเนื้อหาที่จะบันทึก
                string logContent = $"==================================================\n" +
                                    $"Timestamp    : {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                                    $"Prompt       : {prompt}\n" +
                                    $"Response     : {response}\n" +
                                    $"Time Elapsed : {elapsedMs} ms ({elapsedMs / 1000.0:F2} seconds)\n" +
                                    $"==================================================\n\n";

                // เขียนต่อท้ายไฟล์เดิม (Append) แบบ Async
                await System.IO.File.AppendAllTextAsync(logFilePath, logContent);
            }
            catch (Exception ex)
            {
                // ป้องกันไม่ให้แอปพังหากเขียนไฟล์ Log ไม่สำเร็จ
                Console.WriteLine($"Failed to write log file: {ex.Message}");
            }

        }
    }
}

