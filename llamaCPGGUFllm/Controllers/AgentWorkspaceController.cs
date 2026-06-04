using llamaCPGGUFllm.Services;
using Microsoft.AspNetCore.Mvc;

namespace llamaCPGGUFllm.Controllers
{
    public sealed class ChecklistUpdateRequest
    {
        public bool IsDone { get; set; }
    }

    public sealed class ActiveFolderRequest
    {
        public string FolderPath { get; set; } = string.Empty;
    }

    public sealed class RebuildIndexRequest
    {
        public string? FolderPath { get; set; }
    }

    public sealed class GuidanceSelectionRequest
    {
        public List<string>? SelectedSkills { get; set; }
        public List<string>? SelectedRules { get; set; }
    }

    public sealed class RunTestsRequest
    {
        public string? Command { get; set; }
        public int MaxAttempts { get; set; } = 3;
    }

    public sealed class FileCreationSettingsRequest
    {
        public string Mode { get; set; } = "ask-path";
        public string? DefaultFolder { get; set; }
    }

    public sealed class PromptCreateFileRequest
    {
        public string Prompt { get; set; } = string.Empty;
        public string? TargetPath { get; set; }
    }

    public sealed class AgentPipelineRequest
    {
        public string Goal { get; set; } = string.Empty;
        public string? ExecutorPrompt { get; set; }
        public string? TargetPath { get; set; }
        public string? TestCommand { get; set; }
        public int MaxCycles { get; set; } = 3;
    }

    [ApiController]
    [Route("api/[controller]")]
    public class AgentWorkspaceController : ControllerBase
    {
        private readonly AgentWorkspaceService _service;

        public AgentWorkspaceController(AgentWorkspaceService service)
        {
            _service = service;
        }

        [HttpGet("state")]
        public async Task<IActionResult> GetState(CancellationToken ct)
        {
            var state = await _service.GetStateAsync(ct);
            return Ok(state);
        }

        [HttpPost("bootstrap")]
        public async Task<IActionResult> Bootstrap(CancellationToken ct)
        {
            var state = await _service.BootstrapAsync(ct);
            return Ok(state);
        }

        [HttpPost("checklist/{itemId}")]
        public async Task<IActionResult> UpdateChecklist(string itemId, [FromBody] ChecklistUpdateRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return BadRequest("itemId is required");
            }

            var state = await _service.SetChecklistItemAsync(itemId, request.IsDone, ct);
            return Ok(state);
        }

        [HttpPost("active-folder")]
        public async Task<IActionResult> SetActiveFolder([FromBody] ActiveFolderRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.FolderPath))
            {
                return BadRequest("FolderPath is required");
            }

            var state = await _service.SetActiveFolderAsync(request.FolderPath, ct);
            return Ok(state);
        }

        [HttpPost("rebuild-index")]
        public async Task<IActionResult> RebuildIndex([FromBody] RebuildIndexRequest? request, CancellationToken ct)
        {
            var state = await _service.RebuildIndexAsync(request?.FolderPath, ct);
            return Ok(state);
        }

        [HttpGet("search-web")]
        public async Task<IActionResult> SearchWeb([FromQuery] string query, [FromQuery] int limit = 5, CancellationToken ct = default)
        {
            var results = await _service.SearchWebAsync(query, limit, ct);
            return Ok(new
            {
                provider = "duckduckgo",
                query,
                count = results.Count,
                results,
            });
        }

        [HttpPost("guidance/select")]
        public async Task<IActionResult> SelectGuidance([FromBody] GuidanceSelectionRequest request, CancellationToken ct)
        {
            var state = await _service.SetGuidanceSelectionAsync(request.SelectedSkills, request.SelectedRules, ct);
            return Ok(state);
        }

        [HttpPost("tools/run-tests")]
        public async Task<IActionResult> RunTests([FromBody] RunTestsRequest? request, CancellationToken ct)
        {
            var result = await _service.RunTestsWithRetryAsync(request?.Command, request?.MaxAttempts ?? 3, ct);
            return Ok(result);
        }

        [HttpPost("settings/file-create")]
        public async Task<IActionResult> UpdateFileCreateSettings([FromBody] FileCreationSettingsRequest request, CancellationToken ct)
        {
            var state = await _service.SetFileCreationSettingsAsync(request.Mode, request.DefaultFolder, ct);
            return Ok(state);
        }

        [HttpPost("tools/create-file-from-prompt")]
        public async Task<IActionResult> CreateFileFromPrompt([FromBody] PromptCreateFileRequest request, CancellationToken ct)
        {
            var result = await _service.HandlePromptCreateFileAsync(request.Prompt, request.TargetPath, ct);
            return Ok(result);
        }

        [HttpPost("agent/pipeline")]
        public async Task<IActionResult> RunAgentPipeline([FromBody] AgentPipelineRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Goal))
            {
                return BadRequest("Goal is required");
            }

            var result = await _service.RunAgentPipelineAsync(
                request.Goal,
                request.ExecutorPrompt,
                request.TargetPath,
                request.TestCommand,
                request.MaxCycles,
                ct);
            return Ok(result);
        }
    }
}
