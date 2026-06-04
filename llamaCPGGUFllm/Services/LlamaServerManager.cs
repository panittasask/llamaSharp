using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace llamaCPGGUFllm.Services
{
    public record LlamaServerStatus(bool IsRunning, int? ProcessId, string? CurrentModelPath, string? BaseUrl, string? Message);

    public class LlamaServerManager
    {
        private readonly LlmConfiguration _cfg;
        private readonly object _lock = new();
        private Process? _process;
        private string? _currentModelPath;

        public LlamaServerManager(IOptions<LlmConfiguration> config)
        {
            _cfg = config.Value;
        }

        public LlamaServerStatus GetStatus()
        {
            lock (_lock)
            {
                var running = _process is { HasExited: false };
                return new LlamaServerStatus(running, running ? _process!.Id : null, _currentModelPath, _cfg.BaseUrl, null);
            }
        }

        public string[] ListModels()
        {
            if (string.IsNullOrWhiteSpace(_cfg.ModelLocation) || !Directory.Exists(_cfg.ModelLocation))
            {
                return Array.Empty<string>();
            }

            return Directory
                .EnumerateFiles(_cfg.ModelLocation, "*.gguf", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(_cfg.ModelLocation, path).Replace('\\', '/'))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public LlamaServerStatus Start(string? modelPath = null)
        {
            lock (_lock)
            {
                if (_process is { HasExited: false })
                {
                    return new LlamaServerStatus(true, _process.Id, _currentModelPath, _cfg.BaseUrl, "Server is already running.");
                }

                var resolvedModelPath = ResolveModelPath(modelPath);
                if (!File.Exists(resolvedModelPath))
                {
                    return new LlamaServerStatus(false, null, null, _cfg.BaseUrl, $"Model file not found: {resolvedModelPath}");
                }

                var executable = ResolveExecutablePath();
                if (!File.Exists(executable))
                {
                    return new LlamaServerStatus(false, null, null, _cfg.BaseUrl, $"llama-server executable not found: {executable}");
                }

                var args = $"-m \"{resolvedModelPath}\" -ngl {_cfg.GpuLayers} -c {_cfg.ContextSize} --host {_cfg.ServerHost} --port {_cfg.ServerPort}";
                if (!string.IsNullOrWhiteSpace(_cfg.ExtraServerArgs))
                {
                    args += " " + _cfg.ExtraServerArgs;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = args,
                    WorkingDirectory = _cfg.LlamaCppDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _process = Process.Start(psi);
                _currentModelPath = resolvedModelPath;

                if (_process is null)
                {
                    return new LlamaServerStatus(false, null, null, _cfg.BaseUrl, "Failed to start llama-server process.");
                }

                return new LlamaServerStatus(true, _process.Id, _currentModelPath, _cfg.BaseUrl, "Server started.");
            }
        }

        public LlamaServerStatus Stop()
        {
            lock (_lock)
            {
                if (_process is null || _process.HasExited)
                {
                    _process = null;
                    return new LlamaServerStatus(false, null, _currentModelPath, _cfg.BaseUrl, "Server is not running.");
                }

                try
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
                catch
                {
                    // best effort
                }

                _process.Dispose();
                _process = null;
                return new LlamaServerStatus(false, null, _currentModelPath, _cfg.BaseUrl, "Server stopped.");
            }
        }

        public LlamaServerStatus SwitchModel(string modelPath)
        {
            lock (_lock)
            {
                _ = Stop();
                return Start(modelPath);
            }
        }

        private string ResolveExecutablePath()
        {
            if (Path.IsPathRooted(_cfg.LlamaServerExecutable))
            {
                return _cfg.LlamaServerExecutable;
            }
            return Path.Combine(_cfg.LlamaCppDirectory, _cfg.LlamaServerExecutable);
        }

        private string ResolveModelPath(string? requestedModelPath)
        {
            var model = string.IsNullOrWhiteSpace(requestedModelPath) ? _cfg.DefaultModelPath : requestedModelPath;
            if (string.IsNullOrWhiteSpace(model))
            {
                return string.Empty;
            }

            if (Path.IsPathRooted(model))
            {
                return model;
            }

            var normalized = model.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(_cfg.ModelLocation, normalized));
        }
    }
}