using System.Diagnostics;
using llamaCPGGUFllm.Services;
using Microsoft.AspNetCore.Mvc;

namespace llamaCPGGUFllm.Controllers
{
    public record PromptRequest(string Prompt);
    [ApiController]
    [Route("api/[controller]")]
    public class LlmController : ControllerBase
    {
        private readonly LlmService _llmService;

        // ฉีด LlmService เข้ามาใช้งานผ่าน DI
        public LlmController(LlmService llmService)
        {
            _llmService = llmService;
        }

        [HttpPost("stream")]
        public async Task GetStreamAsync([FromBody] PromptRequest request, CancellationToken ct)
        {
            // 1. ตั้งค่า Header สำหรับการทำ Server-Sent Events (SSE)
            Response.ContentType = "text/event-stream";
            Response.Headers.Append("Cache-Control", "no-cache");
            Response.Headers.Append("Connection", "keep-alive");

            try
            {
                // 2. ดึงข้อมูลจาก Service และทยอยพ่นออกไป
                await foreach (var chunk in _llmService.GenerateStreamAsync(request.Prompt, ct))
                {
                    var safeChunk = chunk.Replace("\n", "\\n").Replace("\r", "");

                    await Response.WriteAsync($"data: {safeChunk}\n\n", ct);
                    await Response.Body.FlushAsync(ct); // บังคับให้ส่งข้อมูลออกทันที ไม่ต้องรอ Buffer
                }

                // 3. ส่งสัญญาณจบ Stream
                await Response.WriteAsync("data: [DONE]\n\n", ct);
            }
            catch (OperationCanceledException)
            {
                // ผู้ใช้กดยกเลิก หรือปิดเบราว์เซอร์ระหว่างโหลด
                Console.WriteLine("Streaming connection was canceled by the client.");
            }
        }
        [HttpPost("normal")]
        public async Task<IActionResult> PostNormalAsync([FromBody] PromptRequest request, CancellationToken ct)
        {
            // 1. เริ่มจับเวลา
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // 2. เรียกใช้งาน Service แบบรอคำตอบเต็ม
                string responseText = await _llmService.GenerateFullTextAsync(request.Prompt, ct);

                // 3. หยุดจับเวลาเมื่อได้คำตอบครบถ้วน
                stopwatch.Stop();
                long elapsedMilliseconds = stopwatch.ElapsedMilliseconds;

                // 4. บันทึกข้อมูลลง Log File
                await WriteLogAsync(request.Prompt, responseText, elapsedMilliseconds);

                // 5. ส่งผลลัพธ์กลับไปให้ Client ในรูปแบบ JSON ปกติ
                return Ok(new
                {
                    Response = responseText,
                    TimeElapsedMs = elapsedMilliseconds,
                    TimeElapsedSec = elapsedMilliseconds / 1000.0
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

