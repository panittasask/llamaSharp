using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace llamaCPGGUFllm.Services
{
    public sealed class AgentChecklistItem
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsDone { get; set; }
    }

    public sealed class AgentSessionEvent
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Activity { get; set; } = "acting";
        public string Detail { get; set; } = string.Empty;
        public string Status { get; set; } = "success";
        public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class AgentPlanState
    {
        public string Title { get; set; } = "Agent Workspace Bootstrap Plan";
        public string Goal { get; set; } = "Prepare folder-driven workflow for skills/rules and incremental indexing/embedding.";
        public string ActiveFolderPath { get; set; } = "agent-workspace/skills";
        public List<string> AvailableFolders { get; set; } = new();
        public List<string> AvailableSkills { get; set; } = new();
        public List<string> AvailableRules { get; set; } = new();
        public List<string> SelectedSkills { get; set; } = new();
        public List<string> SelectedRules { get; set; } = new();
        public string FileCreationMode { get; set; } = "ask-path";
        public string DefaultCreateFolder { get; set; } = "agent-workspace/generated";
        public List<AgentChecklistItem> Checklist { get; set; } = new();
        public string? LastIndexFile { get; set; }
        public string? LastEmbeddingFile { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public List<AgentSessionEvent> RecentEvents { get; set; } = new();
        public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class PromptFileActionResult
    {
        public bool RequiresPath { get; set; }
        public bool Created { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? CreatedFilePath { get; set; }
        public string? SuggestedFolder { get; set; }
    }

    public sealed class AgentTestRunResult
    {
        public bool Passed { get; set; }
        public int Attempts { get; set; }
        public string Command { get; set; } = string.Empty;
        public string Output { get; set; } = string.Empty;
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset EndedAtUtc { get; set; }
    }

    public sealed class AgentPipelineCycleLog
    {
        public int Cycle { get; set; }
        public bool Passed { get; set; }
        public string Summary { get; set; } = string.Empty;
    }

    public sealed class AgentPipelineResult
    {
        public bool Completed { get; set; }
        public int CyclesUsed { get; set; }
        public string PlanFilePath { get; set; } = string.Empty;
        public string ChecklistFilePath { get; set; } = string.Empty;
        public string? LastErrorSummary { get; set; }
        public string LastTestOutput { get; set; } = string.Empty;
        public List<AgentPipelineCycleLog> CycleLogs { get; set; } = new();
    }

    public sealed class AgentIndexEntry
    {
        public string RelativePath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public DateTimeOffset LastWriteUtc { get; set; }
        public int LineCount { get; set; }
    }

    public sealed class AgentEmbeddingEntry
    {
        public string RelativePath { get; set; } = string.Empty;
        public float[] Vector { get; set; } = Array.Empty<float>();
    }

    public sealed class WebSearchResult
    {
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Snippet { get; set; } = string.Empty;
        public string Source { get; set; } = "duckduckgo";
    }

    public class AgentWorkspaceService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        private static readonly string[] IndexableExtensions =
        {
            ".md", ".txt", ".json", ".yaml", ".yml", ".cs", ".ts", ".tsx", ".js", ".html", ".scss", ".css", ".xml"
        };

        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly string _workspaceRoot;
        private readonly string _agentRoot;
        private readonly string _skillsPath;
        private readonly string _rulesPath;
        private readonly string _indexesPath;
        private readonly string _embeddingsPath;
        private readonly string _plansPath;
        private readonly string _sessionsPath;
        private readonly string _planFilePath;

        public AgentWorkspaceService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _workspaceRoot = Directory.GetCurrentDirectory();
            _agentRoot = Path.Combine(_workspaceRoot, "agent-workspace");
            _skillsPath = Path.Combine(_agentRoot, "skills");
            _rulesPath = Path.Combine(_agentRoot, "rules");
            _indexesPath = Path.Combine(_agentRoot, "indexes");
            _embeddingsPath = Path.Combine(_agentRoot, "embeddings");
            _plansPath = Path.Combine(_agentRoot, "plans");
            _sessionsPath = Path.Combine(_agentRoot, "sessions");
            _planFilePath = Path.Combine(_agentRoot, "plan-checklist.json");
        }

        public async Task<AgentPipelineResult> RunAgentPipelineAsync(
            string goal,
            string? executorPrompt,
            string? targetPath,
            string? testCommand,
            int maxCycles,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(goal))
            {
                throw new ArgumentException("Goal is required.");
            }

            maxCycles = Math.Clamp(maxCycles, 1, 5);

            await _lock.WaitAsync(ct);
            try
            {
                await EnsureWorkspaceLayoutAsync(ct);
                var plan = await LoadPlanAsync(ct);
                await EnsureChecklistDefaultsAsync(plan, ct);
                await RefreshGuidanceCatalogAsync(plan, ct);

                var (planFilePath, checklistFilePath) = await CreatePlanArtifactsAsync(goal, maxCycles, ct);
                await AddSessionEventAsync(plan, "create plan", $"Planner generated {planFilePath} and {checklistFilePath}", "success", ct);

                var effectivePrompt = string.IsNullOrWhiteSpace(executorPrompt) ? goal : executorPrompt.Trim();
                var finalTestCommand = string.IsNullOrWhiteSpace(testCommand)
                    ? "dotnet test llamaCPGGUFllm.sln"
                    : testCommand.Trim();

                var cycleLogs = new List<AgentPipelineCycleLog>();
                var lastOutput = string.Empty;
                string? lastError = null;
                var completed = false;
                var cyclesUsed = 0;

                for (var cycle = 1; cycle <= maxCycles; cycle++)
                {
                    ct.ThrowIfCancellationRequested();
                    cyclesUsed = cycle;

                    await AddSessionEventAsync(plan, "acting", $"Executor cycle {cycle}/{maxCycles} is applying plan", "running", ct);
                    var createdFile = await CreateOrUpdateFileForPipelineAsync(plan, effectivePrompt, targetPath, cycle, ct);
                    await AddSessionEventAsync(plan, "acting", $"Executor updated file: {createdFile}", "success", ct);

                    await AddSessionEventAsync(plan, "acting", $"Tester cycle {cycle}/{maxCycles} running: {finalTestCommand}", "running", ct);
                    var (exitCode, output) = await RunShellCommandAsync(finalTestCommand, ct);
                    lastOutput = output;
                    var outputTail = SummarizeError(output);
                    await AddSessionEventAsync(plan, "acting", $"Tester cycle {cycle} output: {outputTail}", "running", ct);

                    if (exitCode == 0)
                    {
                        completed = true;
                        MarkChecklist(plan, "run-tests-tool", true);
                        cycleLogs.Add(new AgentPipelineCycleLog
                        {
                            Cycle = cycle,
                            Passed = true,
                            Summary = "Tests passed",
                        });
                        await AddSessionEventAsync(plan, "acting", $"Tester cycle {cycle} passed", "success", ct);
                        break;
                    }

                    lastError = SummarizeError(output);
                    cycleLogs.Add(new AgentPipelineCycleLog
                    {
                        Cycle = cycle,
                        Passed = false,
                        Summary = lastError,
                    });
                    await AddSessionEventAsync(plan, "acting", $"Tester cycle {cycle} failed: {lastError}", "error", ct);
                }

                await SavePlanAsync(plan, ct);
                return new AgentPipelineResult
                {
                    Completed = completed,
                    CyclesUsed = cyclesUsed,
                    PlanFilePath = planFilePath,
                    ChecklistFilePath = checklistFilePath,
                    LastErrorSummary = lastError,
                    LastTestOutput = lastOutput,
                    CycleLogs = cycleLogs,
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<AgentPlanState> GetStateAsync(CancellationToken ct)
        {
            await _lock.WaitAsync(ct);
            try
            {
                await EnsureWorkspaceLayoutAsync(ct);
                var plan = await LoadPlanAsync(ct);
                await EnsureChecklistDefaultsAsync(plan, ct);
                await RefreshGuidanceCatalogAsync(plan, ct);
                return plan;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<AgentPlanState> BootstrapAsync(CancellationToken ct)
        {
            await _lock.WaitAsync(ct);
            try
            {
                await EnsureWorkspaceLayoutAsync(ct);
                var plan = await LoadPlanAsync(ct);
                await EnsureChecklistDefaultsAsync(plan, ct);
                await RefreshGuidanceCatalogAsync(plan, ct);
                MarkChecklist(plan, "create-folders", true);
                await AddSessionEventAsync(plan, "create plan", "Bootstrap workspace folders and initial plan", "success", ct);
                await SavePlanAsync(plan, ct);
                return plan;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<AgentPlanState> SetChecklistItemAsync(string itemId, bool isDone, CancellationToken ct)
        {
            await _lock.WaitAsync(ct);
            try
            {
                var plan = await LoadPlanAsync(ct);
                await EnsureChecklistDefaultsAsync(plan, ct);
                await RefreshGuidanceCatalogAsync(plan, ct);
                MarkChecklist(plan, itemId, isDone);
                await AddSessionEventAsync(plan, "acting", $"Checklist item '{itemId}' set to {(isDone ? "done" : "not done")}", "success", ct);
                await SavePlanAsync(plan, ct);
                return plan;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<AgentPlanState> SetActiveFolderAsync(string folderPath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                throw new ArgumentException("FolderPath is required.");
            }

            await _lock.WaitAsync(ct);
            try
            {
                var plan = await LoadPlanAsync(ct);
                await EnsureChecklistDefaultsAsync(plan, ct);
                await RefreshGuidanceCatalogAsync(plan, ct);
                var normalizedRelative = NormalizeFolderToRelative(folderPath);
                var absolute = Path.Combine(_workspaceRoot, normalizedRelative.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(absolute))
                {
                    throw new DirectoryNotFoundException($"Folder does not exist: {normalizedRelative}");
                }

                if (!plan.AvailableFolders.Contains(normalizedRelative, StringComparer.OrdinalIgnoreCase))
                {
                    plan.AvailableFolders.Add(normalizedRelative);
                }

                plan.ActiveFolderPath = normalizedRelative;
                await AddSessionEventAsync(plan, "acting", $"Active folder changed to '{normalizedRelative}'", "success", ct);
                await SavePlanAsync(plan, ct);
                return plan;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<AgentPlanState> RebuildIndexAsync(string? folderPath, CancellationToken ct)
        {
            await _lock.WaitAsync(ct);
            try
            {
                var plan = await LoadPlanAsync(ct);
                await EnsureChecklistDefaultsAsync(plan, ct);
                await RefreshGuidanceCatalogAsync(plan, ct);

                var targetRelative = string.IsNullOrWhiteSpace(folderPath) ? plan.ActiveFolderPath : NormalizeFolderToRelative(folderPath);
                var targetAbsolute = Path.Combine(_workspaceRoot, targetRelative.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(targetAbsolute))
                {
                    throw new DirectoryNotFoundException($"Folder does not exist: {targetRelative}");
                }

                var guidanceSummary = await BuildGuidanceSummaryAsync(plan, ct);
                await AddSessionEventAsync(plan, "acting", $"Indexing files from '{targetRelative}' with guidance: {guidanceSummary}", "running", ct);

                var files = Directory
                    .EnumerateFiles(targetAbsolute, "*", SearchOption.AllDirectories)
                    .Where(path => IndexableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    .ToArray();

                var indexEntries = new List<AgentIndexEntry>(files.Length);
                var embeddings = new List<AgentEmbeddingEntry>(files.Length);

                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    if (info.Length > 1_000_000)
                    {
                        continue;
                    }

                    string[] lines;
                    try
                    {
                        lines = await File.ReadAllLinesAsync(file, ct);
                    }
                    catch
                    {
                        continue;
                    }

                    var relative = Path.GetRelativePath(_workspaceRoot, file).Replace('\\', '/');
                    indexEntries.Add(new AgentIndexEntry
                    {
                        RelativePath = relative,
                        SizeBytes = info.Length,
                        LastWriteUtc = info.LastWriteTimeUtc,
                        LineCount = lines.Length,
                    });

                    embeddings.Add(new AgentEmbeddingEntry
                    {
                        RelativePath = relative,
                        Vector = BuildPseudoEmbedding(relative, lines),
                    });
                }

                var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
                var indexFileName = $"index-{stamp}.json";
                var embeddingFileName = $"embedding-{stamp}.json";
                var indexFullPath = Path.Combine(_indexesPath, indexFileName);
                var embeddingFullPath = Path.Combine(_embeddingsPath, embeddingFileName);

                await File.WriteAllTextAsync(indexFullPath, JsonSerializer.Serialize(indexEntries, JsonOptions), ct);
                await File.WriteAllTextAsync(embeddingFullPath, JsonSerializer.Serialize(embeddings, JsonOptions), ct);

                plan.LastIndexFile = Path.GetRelativePath(_workspaceRoot, indexFullPath).Replace('\\', '/');
                plan.LastEmbeddingFile = Path.GetRelativePath(_workspaceRoot, embeddingFullPath).Replace('\\', '/');
                plan.ActiveFolderPath = targetRelative;
                MarkChecklist(plan, "index-files", true);
                MarkChecklist(plan, "build-embeddings", true);
                await AddSessionEventAsync(plan, "acting", $"Indexed {indexEntries.Count} files and generated embeddings", "success", ct);
                await SavePlanAsync(plan, ct);
                return plan;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<AgentPlanState> SetGuidanceSelectionAsync(IEnumerable<string>? selectedSkills, IEnumerable<string>? selectedRules, CancellationToken ct)
        {
            await _lock.WaitAsync(ct);
            try
            {
                var plan = await LoadPlanAsync(ct);
                await EnsureChecklistDefaultsAsync(plan, ct);
                await RefreshGuidanceCatalogAsync(plan, ct);

                plan.SelectedSkills = NormalizeGuidanceSelection(selectedSkills, plan.AvailableSkills, _skillsPath);
                plan.SelectedRules = NormalizeGuidanceSelection(selectedRules, plan.AvailableRules, _rulesPath);

                MarkChecklist(plan, "select-guidance", true);
                await AddSessionEventAsync(
                    plan,
                    "acting",
                    $"Guidance selection updated ({plan.SelectedSkills.Count} skills, {plan.SelectedRules.Count} rules)",
                    "success",
                    ct);
                await SavePlanAsync(plan, ct);
                return plan;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<AgentPlanState> SetFileCreationSettingsAsync(string mode, string? defaultFolder, CancellationToken ct)
        {
            await _lock.WaitAsync(ct);
            try
            {
                var plan = await LoadPlanAsync(ct);
                await EnsureChecklistDefaultsAsync(plan, ct);
                await RefreshGuidanceCatalogAsync(plan, ct);

                var normalizedMode = string.Equals(mode, "auto-create", StringComparison.OrdinalIgnoreCase)
                    ? "auto-create"
                    : "ask-path";

                plan.FileCreationMode = normalizedMode;
                var folderToUse = string.IsNullOrWhiteSpace(defaultFolder)
                    ? plan.DefaultCreateFolder
                    : NormalizeFolderToRelative(defaultFolder);

                var absoluteFolder = Path.Combine(_workspaceRoot, folderToUse.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(absoluteFolder);
                plan.DefaultCreateFolder = folderToUse;

                await AddSessionEventAsync(
                    plan,
                    "acting",
                    $"File creation settings updated ({plan.FileCreationMode}, folder={plan.DefaultCreateFolder})",
                    "success",
                    ct);
                await SavePlanAsync(plan, ct);
                return plan;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<PromptFileActionResult> HandlePromptCreateFileAsync(string prompt, string? targetPath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                throw new ArgumentException("Prompt is required.");
            }

            await _lock.WaitAsync(ct);
            try
            {
                var plan = await LoadPlanAsync(ct);
                await EnsureChecklistDefaultsAsync(plan, ct);
                await RefreshGuidanceCatalogAsync(plan, ct);

                var candidatePath = string.IsNullOrWhiteSpace(targetPath)
                    ? TryExtractLikelyPath(prompt)
                    : targetPath.Trim();

                if (string.IsNullOrWhiteSpace(candidatePath) && plan.FileCreationMode == "ask-path")
                {
                    await AddSessionEventAsync(
                        plan,
                        "thinking",
                        "Prompt requested file creation but no path was provided.",
                        "waiting",
                        ct);
                    await SavePlanAsync(plan, ct);
                    return new PromptFileActionResult
                    {
                        RequiresPath = true,
                        Created = false,
                        Message = "Please specify file path before creating file.",
                        SuggestedFolder = plan.DefaultCreateFolder,
                    };
                }

                var absolutePath = ResolveTargetFileSystemPath(plan, candidatePath);
                var relativePath = DescribeFilePath(absolutePath);
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

                if (!File.Exists(absolutePath))
                {
                    var guidanceSummary = await BuildGuidanceSummaryAsync(plan, ct);
                    var template =
                        "# Generated File\n\n"
                        + $"Created from prompt at {DateTimeOffset.UtcNow:O}.\n\n"
                        + "## Original Prompt\n"
                        + prompt.Trim() + "\n\n"
                        + "## Guidance Summary\n"
                        + guidanceSummary + "\n";
                    await File.WriteAllTextAsync(absolutePath, template, ct);
                }

                await AddSessionEventAsync(
                    plan,
                    "acting",
                    $"Created file from prompt: {relativePath}",
                    "success",
                    ct);
                await SavePlanAsync(plan, ct);
                return new PromptFileActionResult
                {
                    RequiresPath = false,
                    Created = true,
                    CreatedFilePath = relativePath,
                    Message = $"File created: {relativePath}",
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<AgentTestRunResult> RunTestsWithRetryAsync(string? command, int maxAttempts, CancellationToken ct)
        {
            maxAttempts = Math.Clamp(maxAttempts, 1, 5);

            await _lock.WaitAsync(ct);
            try
            {
                var plan = await LoadPlanAsync(ct);
                await EnsureChecklistDefaultsAsync(plan, ct);
                await RefreshGuidanceCatalogAsync(plan, ct);

                var commandToRun = string.IsNullOrWhiteSpace(command)
                    ? "dotnet test llamaCPGGUFllm.sln"
                    : command.Trim();

                var outputBuilder = new StringBuilder();
                var startedAt = DateTimeOffset.UtcNow;
                var passed = false;
                var attemptsUsed = 0;
                var guidanceSummary = await BuildGuidanceSummaryAsync(plan, ct);

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    attemptsUsed = attempt;
                    await AddSessionEventAsync(
                        plan,
                        "acting",
                        $"Run tests attempt {attempt}/{maxAttempts} using guidance: {guidanceSummary}",
                        "running",
                        ct);

                    var attemptResult = await RunShellCommandAsync(commandToRun, ct);
                    outputBuilder.AppendLine($"=== Attempt {attempt}/{maxAttempts} ===");
                    outputBuilder.AppendLine(attemptResult.Output);
                    outputBuilder.AppendLine();

                    if (attemptResult.ExitCode == 0)
                    {
                        passed = true;
                        break;
                    }
                }

                var endedAt = DateTimeOffset.UtcNow;
                if (passed)
                {
                    MarkChecklist(plan, "run-tests-tool", true);
                    await AddSessionEventAsync(plan, "acting", $"Tests passed after {attemptsUsed} attempt(s)", "success", ct);
                }
                else
                {
                    await AddSessionEventAsync(plan, "acting", $"Tests still failing after {attemptsUsed} attempt(s)", "error", ct);
                }

                await SavePlanAsync(plan, ct);
                return new AgentTestRunResult
                {
                    Passed = passed,
                    Attempts = attemptsUsed,
                    Command = commandToRun,
                    Output = outputBuilder.ToString(),
                    StartedAtUtc = startedAt,
                    EndedAtUtc = endedAt,
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<IReadOnlyList<WebSearchResult>> SearchWebAsync(string query, int limit, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                throw new ArgumentException("Query is required.");
            }

            limit = Math.Clamp(limit, 1, 10);

            var results = new List<WebSearchResult>();
            var requestUrl = $"https://api.duckduckgo.com/?q={Uri.EscapeDataString(query)}&format=json&no_html=1&skip_disambig=1";
            var payload = await _httpClient.GetStringAsync(requestUrl, ct);

            using var doc = JsonDocument.Parse(payload);
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
                    else if (item.TryGetProperty("Topics", out var nestedTopics)
                        && nestedTopics.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var nested in nestedTopics.EnumerateArray())
                        {
                            if (results.Count >= limit)
                            {
                                break;
                            }

                            if (nested.TryGetProperty("FirstURL", out var nestedUrl)
                                && nested.TryGetProperty("Text", out var nestedText))
                            {
                                var url = nestedUrl.GetString() ?? string.Empty;
                                var text = nestedText.GetString() ?? string.Empty;
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
            }

            if (results.Count == 0)
            {
                results.Add(new WebSearchResult
                {
                    Title = $"Search DuckDuckGo for: {query}",
                    Url = $"https://duckduckgo.com/?q={Uri.EscapeDataString(query)}",
                    Snippet = "No direct snippet from API. Open result page for full search output.",
                    Source = "duckduckgo",
                });
            }

            return results.Take(limit).ToList();
        }

        private async Task<(int ExitCode, string Output)> RunShellCommandAsync(string command, CancellationToken ct)
        {
            var isWindows = OperatingSystem.IsWindows();
            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                Arguments = isWindows
                    ? $"/c {command}"
                    : $"-lc \"{command.Replace("\"", "\\\"")}\"",
                WorkingDirectory = _workspaceRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
            var outputBuilder = new StringBuilder();

            process.Start();

            var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stdErrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            outputBuilder.AppendLine(await stdOutTask);
            outputBuilder.AppendLine(await stdErrTask);

            return (process.ExitCode, outputBuilder.ToString());
        }

        private async Task EnsureWorkspaceLayoutAsync(CancellationToken ct)
        {
            Directory.CreateDirectory(_agentRoot);
            Directory.CreateDirectory(_skillsPath);
            Directory.CreateDirectory(_rulesPath);
            Directory.CreateDirectory(_indexesPath);
            Directory.CreateDirectory(_embeddingsPath);
            Directory.CreateDirectory(_plansPath);
            Directory.CreateDirectory(_sessionsPath);

            var skillReadmePath = Path.Combine(_skillsPath, "README.md");
            if (!File.Exists(skillReadmePath))
            {
                await File.WriteAllTextAsync(skillReadmePath,
                    "# Skills Folder\n\nStore task-specific skill files here.\n", ct);
            }

            var uxSkillPath = Path.Combine(_skillsPath, "ux-ui-modern.skill.md");
            if (!File.Exists(uxSkillPath))
            {
                await File.WriteAllTextAsync(uxSkillPath,
                    "# UX UI Modern Skill\n\n- Prefer clear hierarchy and spacing.\n- Keep forms concise and actionable.\n- Always show loading indicators for async actions.\n", ct);
            }

            var ruleReadmePath = Path.Combine(_rulesPath, "README.md");
            if (!File.Exists(ruleReadmePath))
            {
                await File.WriteAllTextAsync(ruleReadmePath,
                    "# Rules Folder\n\nStore instruction/rule files here.\n", ct);
            }

            var defaultRulePath = Path.Combine(_rulesPath, "agent-behavior.rule.md");
            if (!File.Exists(defaultRulePath))
            {
                await File.WriteAllTextAsync(defaultRulePath,
                    "# Agent Behavior Rule\n\n- Read selected skill and rule files before acting.\n- Log each major action into session events.\n- Prefer streaming output for chat responses.\n", ct);
            }

            if (!File.Exists(_planFilePath))
            {
                var defaultPlan = BuildDefaultPlan();
                await SavePlanAsync(defaultPlan, ct);
            }
        }

        private AgentPlanState BuildDefaultPlan()
        {
            return new AgentPlanState
            {
                AvailableFolders = new List<string>
                {
                    "agent-workspace/skills",
                    "agent-workspace/rules",
                },
                AvailableSkills = new List<string>(),
                AvailableRules = new List<string>(),
                SelectedSkills = new List<string>(),
                SelectedRules = new List<string>(),
                FileCreationMode = "ask-path",
                DefaultCreateFolder = "agent-workspace/generated",
                Checklist = new List<AgentChecklistItem>
                {
                    new() { Id = "create-folders", Title = "Create skills and rules folders", Description = "Initialize folders for skills and instruction/rule documents.", IsDone = true },
                    new() { Id = "build-agent-api", Title = "Create agent workspace API", Description = "Expose endpoints for loading plan/checklist and folder selection.", IsDone = false },
                    new() { Id = "add-ui-checklist", Title = "Show checklist in UI", Description = "Render checklist in settings page and support interaction.", IsDone = false },
                    new() { Id = "index-files", Title = "Create file index from selected folder", Description = "Read files in selected folder and create searchable index metadata.", IsDone = false },
                    new() { Id = "build-embeddings", Title = "Build embedding store", Description = "Generate embedding vectors from indexed files for future retrieval.", IsDone = false },
                    new() { Id = "session-store", Title = "Persist session timeline", Description = "Store agent session events for activity history.", IsDone = false },
                    new() { Id = "internet-search-tool", Title = "Provide internet search tool", Description = "Add DuckDuckGo-powered web search endpoint.", IsDone = false },
                    new() { Id = "run-tests-tool", Title = "Provide run test terminal tool", Description = "Run tests with retry loop and session logging.", IsDone = false },
                    new() { Id = "select-guidance", Title = "Select skills and rules", Description = "Choose which skill/rule files should guide agent workflows.", IsDone = false },
                },
            };
        }

        private async Task<AgentPlanState> LoadPlanAsync(CancellationToken ct)
        {
            if (!File.Exists(_planFilePath))
            {
                return BuildDefaultPlan();
            }

            try
            {
                var json = await File.ReadAllTextAsync(_planFilePath, ct);
                var plan = JsonSerializer.Deserialize<AgentPlanState>(json, JsonOptions);
                return plan ?? BuildDefaultPlan();
            }
            catch
            {
                return BuildDefaultPlan();
            }
        }

        private async Task SavePlanAsync(AgentPlanState plan, CancellationToken ct)
        {
            plan.UpdatedAtUtc = DateTimeOffset.UtcNow;
            var json = JsonSerializer.Serialize(plan, JsonOptions);
            await File.WriteAllTextAsync(_planFilePath, json, ct);
        }

        private async Task EnsureChecklistDefaultsAsync(AgentPlanState plan, CancellationToken ct)
        {
            var defaults = BuildDefaultPlan();
            foreach (var folder in defaults.AvailableFolders)
            {
                if (!plan.AvailableFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                {
                    plan.AvailableFolders.Add(folder);
                }
            }

            foreach (var defaultItem in defaults.Checklist)
            {
                if (!plan.Checklist.Any(item => item.Id.Equals(defaultItem.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    plan.Checklist.Add(defaultItem);
                }
            }

            if (string.IsNullOrWhiteSpace(plan.SessionId))
            {
                plan.SessionId = $"session-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
                await AddSessionEventAsync(plan, "create plan", "Session initialized", "success", ct);
            }

            MarkChecklist(plan, "session-store", true);
            MarkChecklist(plan, "internet-search-tool", true);
            if (string.IsNullOrWhiteSpace(plan.FileCreationMode))
            {
                plan.FileCreationMode = "ask-path";
            }

            if (string.IsNullOrWhiteSpace(plan.DefaultCreateFolder))
            {
                plan.DefaultCreateFolder = "agent-workspace/generated";
            }
        }

        private async Task RefreshGuidanceCatalogAsync(AgentPlanState plan, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            plan.AvailableSkills = Directory
                .EnumerateFiles(_skillsPath, "*", SearchOption.TopDirectoryOnly)
                .Where(path =>
                    path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                .Select(path => Path.GetRelativePath(_workspaceRoot, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            plan.AvailableRules = Directory
                .EnumerateFiles(_rulesPath, "*", SearchOption.TopDirectoryOnly)
                .Where(path =>
                    path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                .Select(path => Path.GetRelativePath(_workspaceRoot, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (plan.SelectedSkills.Count == 0 && plan.AvailableSkills.Count > 0)
            {
                plan.SelectedSkills = new List<string> { plan.AvailableSkills[0] };
            }

            if (plan.SelectedRules.Count == 0 && plan.AvailableRules.Count > 0)
            {
                plan.SelectedRules = new List<string> { plan.AvailableRules[0] };
            }

            plan.SelectedSkills = NormalizeGuidanceSelection(plan.SelectedSkills, plan.AvailableSkills, _skillsPath);
            plan.SelectedRules = NormalizeGuidanceSelection(plan.SelectedRules, plan.AvailableRules, _rulesPath);
            await SavePlanAsync(plan, ct);
        }

        private List<string> NormalizeGuidanceSelection(
            IEnumerable<string>? selected,
            IEnumerable<string> available,
            string requiredFolder)
        {
            var availableSet = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            foreach (var item in selected ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    continue;
                }

                var relative = NormalizeFolderToRelative(item).Replace('\\', '/');
                var absolute = Path.Combine(_workspaceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!absolute.StartsWith(requiredFolder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (availableSet.Contains(relative))
                {
                    result.Add(relative);
                }
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private async Task<string> BuildGuidanceSummaryAsync(AgentPlanState plan, CancellationToken ct)
        {
            var selected = plan.SelectedSkills.Concat(plan.SelectedRules).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (selected.Length == 0)
            {
                return "none";
            }

            var snippets = new List<string>();
            foreach (var relative in selected)
            {
                var absolute = Path.Combine(_workspaceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(absolute))
                {
                    continue;
                }

                var content = await File.ReadAllTextAsync(absolute, ct);
                var summary = content.Replace("\r", " ").Replace("\n", " ").Trim();
                if (summary.Length > 70)
                {
                    summary = summary[..70] + "...";
                }

                snippets.Add($"{relative}: {summary}");
            }

            return snippets.Count == 0
                ? "none"
                : string.Join(" | ", snippets);
        }

        private string ResolveTargetFileSystemPath(AgentPlanState plan, string? requestedPath)
        {
            var safeRequestedPath = requestedPath?.Trim();
            if (!string.IsNullOrWhiteSpace(safeRequestedPath))
            {
                return Path.GetFullPath(safeRequestedPath, _workspaceRoot);
            }

            var safeFolder = NormalizeFolderToRelative(plan.DefaultCreateFolder);
            var fileName = $"generated-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.md";
            return Path.GetFullPath(Path.Combine(_workspaceRoot, safeFolder, fileName));
        }

        private string DescribeFilePath(string absolutePath)
        {
            if (absolutePath.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetRelativePath(_workspaceRoot, absolutePath).Replace('\\', '/');
            }

            return absolutePath;
        }

        private static string? TryExtractLikelyPath(string prompt)
        {
            var markers = new[] { "path:", "file:", "ที่", "ไฟล์" };
            foreach (var marker in markers)
            {
                var idx = prompt.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    continue;
                }

                var candidate = prompt[(idx + marker.Length)..].Trim();
                if (candidate.Length == 0)
                {
                    continue;
                }

                var firstLine = candidate.Split('\n', '\r').FirstOrDefault()?.Trim();
                if (!string.IsNullOrWhiteSpace(firstLine))
                {
                    return firstLine;
                }
            }

            return null;
        }

        private static void MarkChecklist(AgentPlanState plan, string itemId, bool isDone)
        {
            var item = plan.Checklist.FirstOrDefault(x => x.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                plan.Checklist.Add(new AgentChecklistItem
                {
                    Id = itemId,
                    Title = itemId,
                    Description = itemId,
                    IsDone = isDone,
                });
                return;
            }

            item.IsDone = isDone;
        }

        private async Task AddSessionEventAsync(AgentPlanState plan, string activity, string detail, string status, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(plan.SessionId))
            {
                plan.SessionId = $"session-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
            }

            var evt = new AgentSessionEvent
            {
                Activity = activity,
                Detail = detail,
                Status = status,
                TimestampUtc = DateTimeOffset.UtcNow,
            };

            plan.RecentEvents.Insert(0, evt);
            if (plan.RecentEvents.Count > 30)
            {
                plan.RecentEvents = plan.RecentEvents.Take(30).ToList();
            }

            var sessionFilePath = Path.Combine(_sessionsPath, $"{plan.SessionId}.jsonl");
            var line = JsonSerializer.Serialize(evt, JsonOptions);
            await File.AppendAllTextAsync(sessionFilePath, line + Environment.NewLine, ct);
            await SavePlanAsync(plan, ct);
        }

        private string NormalizeFolderToRelative(string folderPath)
        {
            var trimmed = folderPath.Trim();
            var resolved = Path.IsPathRooted(trimmed)
                ? Path.GetFullPath(trimmed)
                : Path.GetFullPath(Path.Combine(_workspaceRoot, trimmed));

            if (!resolved.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Selected folder must be inside the workspace root.");
            }

            return Path.GetRelativePath(_workspaceRoot, resolved).Replace('\\', '/');
        }

        private static float[] BuildPseudoEmbedding(string relativePath, string[] lines)
        {
            using var sha = SHA256.Create();
            var text = relativePath + "\n" + string.Join('\n', lines.Take(30));
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            var vector = new float[8];
            for (int i = 0; i < vector.Length; i++)
            {
                vector[i] = hash[i] / 255f;
            }

            return vector;
        }

        private async Task<(string planFilePath, string checklistFilePath)> CreatePlanArtifactsAsync(string goal, int maxCycles, CancellationToken ct)
        {
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var planRelative = Path.Combine("agent-workspace", "plans", $"plan-{stamp}.md").Replace('\\', '/');
            var checklistRelative = Path.Combine("agent-workspace", "plans", $"checklist-{stamp}.md").Replace('\\', '/');

            var planAbsolute = Path.Combine(_workspaceRoot, planRelative.Replace('/', Path.DirectorySeparatorChar));
            var checklistAbsolute = Path.Combine(_workspaceRoot, checklistRelative.Replace('/', Path.DirectorySeparatorChar));

            var plannerDoc =
                "# Agent Plan\n\n"
                + $"Goal: {goal}\n\n"
                + "## Pipeline\n"
                + "1. Planner creates checklist.\n"
                + "2. Executor applies changes based on plan.\n"
                + $"3. Tester runs command up to {maxCycles} cycle(s).\n";

            var checklistDoc =
                "# Execution Checklist\n\n"
                + "- [x] Planner created plan and checklist\n"
                + "- [ ] Executor created or updated target file\n"
                + "- [ ] Tester command passed\n"
                + "- [ ] If failed, send error to executor and retry\n";

            await File.WriteAllTextAsync(planAbsolute, plannerDoc, ct);
            await File.WriteAllTextAsync(checklistAbsolute, checklistDoc, ct);
            return (planRelative, checklistRelative);
        }

        private async Task<string> CreateOrUpdateFileForPipelineAsync(
            AgentPlanState plan,
            string executorPrompt,
            string? targetPath,
            int cycle,
            CancellationToken ct)
        {
            var requestedPath = string.IsNullOrWhiteSpace(targetPath) ? TryExtractLikelyPath(executorPrompt) : targetPath;
            var absolutePath = ResolveTargetFileSystemPath(plan, requestedPath);
            var relativePath = DescribeFilePath(absolutePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

            var existing = File.Exists(absolutePath) ? await File.ReadAllTextAsync(absolutePath, ct) : string.Empty;
            var content = existing
                + $"\n\n## Executor Cycle {cycle}\n"
                + $"Prompt: {executorPrompt}\n"
                + $"Timestamp: {DateTimeOffset.UtcNow:O}\n";

            await File.WriteAllTextAsync(absolutePath, content.TrimStart(), ct);
            return relativePath;
        }

        private static string SummarizeError(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return "No output from test runner";
            }

            var lines = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Reverse()
                .Take(8)
                .Reverse();

            return string.Join(" | ", lines).Trim();
        }
    }
}
