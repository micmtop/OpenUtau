using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Serilog;

namespace OpenUtau.Core.Diffical {
    /// <summary>Diffical HTTP transport. Cancellation and backend failures never masquerade as successful empty audio.</summary>
    public class DifficalClient {
        static readonly Lazy<DifficalClient> instance = new(() => new DifficalClient(
            new Uri(Environment.GetEnvironmentVariable("DIFFICAL_SERVER_URL") ?? "http://127.0.0.1:8573/"),
            autoStart: true));
        public static DifficalClient Instance => instance.Value;
        const int MaxRetries = 2;
        const int ServerStartTimeoutMs = 95000;
        static readonly JsonSerializerOptions JsonOptions = new() {
            IncludeFields = true, PropertyNameCaseInsensitive = true,
        };
        readonly HttpClient httpClient;
        readonly Uri endpoint;
        readonly bool autoStart;
        readonly SemaphoreSlim startLock = new(1, 1);
        Process serverProcess;

        /// <summary>Use an already running server by default; only the application singleton starts a local backend.</summary>
        public DifficalClient(Uri endpoint, HttpClient httpClient = null, bool autoStart = false) {
            if (endpoint == null || !endpoint.IsAbsoluteUri ||
                    (endpoint.Scheme != "http" && endpoint.Scheme != "https") ||
                    (autoStart && !endpoint.IsLoopback)) {
                throw new ArgumentException("Use an HTTP endpoint; automatic startup requires a loopback server.");
            }
            this.endpoint = endpoint;
            this.autoStart = autoStart;
            this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        }

        public async Task<DifficalRenderResponse> Render(DifficalRenderRequest request, CancellationToken cancellationToken = default) {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (autoStart) await EnsureServerRunning(cancellationToken);
            string json = JsonSerializer.Serialize(request, JsonOptions);
            for (int attempt = 0; ; attempt++) {
                try {
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var response = await httpClient.PostAsync(new Uri(endpoint, "render"), content, cancellationToken);
                    string body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode) {
                        if ((int)response.StatusCode >= 500 && attempt < MaxRetries) {
                            await Task.Delay(1000, cancellationToken);
                            continue;
                        }
                        throw new HttpRequestException($"Diffical HTTP {(int)response.StatusCode}: {body}", null, response.StatusCode);
                    }
                    var result = JsonSerializer.Deserialize<DifficalRenderResponse>(body, JsonOptions);
                    if (result?.samples == null || result.samples.Length == 0 ||
                            Array.Exists(result.samples, sample => !float.IsFinite(sample))) {
                        throw new InvalidDataException("Diffical returned empty or invalid audio.");
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    return result;
                } catch (HttpRequestException error) when (error.StatusCode == null && attempt < MaxRetries) {
                    Log.Warning(error, "Diffical transport failed; retrying");
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }

        async Task EnsureServerRunning(CancellationToken token) {
            if (await IsServerHealthy(token)) return;
            await startLock.WaitAsync(token);
            try {
                if (await IsServerHealthy(token)) return;
                StartServer();
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < ServerStartTimeoutMs) {
                    token.ThrowIfCancellationRequested();
                    if (await IsServerHealthy(token)) return;
                    if (serverProcess == null || serverProcess.HasExited) {
                        throw new InvalidOperationException("Diffical backend did not start. Set DIFFICAL_BACKEND_PATH and DIFFICAL_PYTHON.");
                    }
                    await Task.Delay(500, token);
                }
                throw new TimeoutException("Diffical backend startup timed out.");
            } finally {
                startLock.Release();
            }
        }

        async Task<bool> IsServerHealthy(CancellationToken token) {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try {
                using var response = await httpClient.GetAsync(endpoint, timeout.Token);
                return response.IsSuccessStatusCode;
            } catch (OperationCanceledException) when (!token.IsCancellationRequested) {
                return false;
            } catch (HttpRequestException) {
                return false;
            }
        }

        private void StartServer() {
            string backendPath = FindBackendPath();
            if (backendPath == null) {
                Log.Error("找不到 Diffical 后端");
                return;
            }

            string rustBinary = FindRustBackendBinary(backendPath);
            if (rustBinary != null) {
                StartRustServer(rustBinary, backendPath);
                return;
            }

            // 必须以模块方式启动（server.py 内部使用相对导入）：
            //   python -m backend.server  （工作目录 = 项目根目录，即 backend 的父目录）
            // 旧实现直接运行 server.py 会抛 "attempted relative import with no known
            // parent package"，导致自动启动失败（日志 17:52:49 ImportError）。
            var projectRoot = Directory.GetParent(backendPath)?.FullName ?? backendPath;
            var psi = new ProcessStartInfo {
                FileName = GetPythonExecutable(),
                Arguments = "-m backend.server",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = projectRoot,
            };
            // librosa/numba 在 HOME 不可写或受限环境下会因缓存定位失败崩溃
            // （"cannot cache function ... no locator available"），显式给一个可写缓存目录。
            string numbaCache = Environment.GetEnvironmentVariable("NUMBA_CACHE_DIR") ??
                Path.Combine(PathManager.Inst.CachePath, "Diffical", "Numba");
            Directory.CreateDirectory(numbaCache);
            psi.EnvironmentVariables["NUMBA_CACHE_DIR"] = numbaCache;
            psi.EnvironmentVariables["KMP_DUPLICATE_LIB_OK"] = "TRUE";

            try {
                serverProcess = Process.Start(psi);
                if (serverProcess != null) {
                    Log.Information($"Diffical 后端进程已启动 (PID={serverProcess.Id})");
                    // 异步读取输出，避免缓冲区满导致阻塞
                    _ = Task.Run(() => {
                        try {
                            while (!serverProcess.StandardOutput.EndOfStream) {
                                var line = serverProcess.StandardOutput.ReadLine();
                                if (!string.IsNullOrEmpty(line)) {
                                    Log.Information($"[Diffical] {line}");
                                }
                            }
                        } catch { }
                    });
                    _ = Task.Run(() => {
                        try {
                            while (!serverProcess.StandardError.EndOfStream) {
                                var line = serverProcess.StandardError.ReadLine();
                                if (!string.IsNullOrEmpty(line)) {
                                    Log.Error($"[Diffical] {line}");
                                }
                            }
                        } catch { }
                    });
                }
            } catch (Exception e) {
                Log.Error(e, "启动 Diffical 后端失败");
            }
        }

        /// <summary>
        /// 查找 Python 后端路径
        /// </summary>
        private string FindBackendPath() {
            // 搜索顺序：
            // 1. 环境变量 DIFFICAL_BACKEND_PATH
            // 2. OpenUtau 根目录下的 diffical/backend
            // 3. OpenUtau 根目录下的 backend
            // 4. 用户目录 ~/.diffical/backend
            // 5. 从可执行文件目录向上查找 diffical/backend 或 backend（开发环境）

            string envPath = Environment.GetEnvironmentVariable("DIFFICAL_BACKEND_PATH") ?? "";
            if (!string.IsNullOrEmpty(envPath) && File.Exists(Path.Combine(envPath, "server.py"))) {
                return envPath;
            }

            string rootPath = PathManager.Inst.RootPath;

            // One-click package layout: backend lives next to the app binary.
            foreach (var bundled in BundledBackendCandidates()) {
                if (!string.IsNullOrEmpty(bundled) && File.Exists(Path.Combine(bundled, "server.py")))
                    return bundled;
            }

            string[] candidates = {
                Path.Combine(rootPath, "diffical", "backend"),
                Path.Combine(rootPath, "backend"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".diffical", "backend"),
            };

            foreach (var path in candidates) {
                if (!string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "server.py"))) {
                    return path;
                }
            }

            // 开发环境：从可执行文件目录向上查找，最多 10 级
            // （.app 包结构会额外增加 Contents/MacOS 等层级）
            try {
                var dir = new DirectoryInfo(rootPath);
                for (int i = 0; i < 10 && dir != null; i++) {
                    string candidate1 = Path.Combine(dir.FullName, "diffical", "backend");
                    string candidate2 = Path.Combine(dir.FullName, "backend");
                    if (File.Exists(Path.Combine(candidate1, "server.py"))) {
                        return candidate1;
                    }
                    if (File.Exists(Path.Combine(candidate2, "server.py"))) {
                        return candidate2;
                    }
                    dir = dir.Parent;
                }
            } catch { }

            return null;
        }

        /// <summary>
        /// 获取 Python 可执行文件路径
        /// </summary>
        private string GetPythonExecutable() {
            // 优先使用环境变量指定的 Python
            string envPython = Environment.GetEnvironmentVariable("DIFFICAL_PYTHON");
            if (!string.IsNullOrEmpty(envPython) && File.Exists(envPython)) {
                return envPython;
            }

            // 尝试常见的 Python 命令，优先选能 import aiohttp 的
            // （server.py 依赖 aiohttp；homebrew python3 通常没有，
            //   基础 anaconda python 有）。逐个探测，失败才用下一个。
            string[] commands = {
                "python3",
                "python",
                "/opt/anaconda3/bin/python",
                "/usr/local/bin/python3",
            };

            foreach (var cmd in commands) {
                try {
                    var psi = new ProcessStartInfo {
                        FileName = cmd,
                        Arguments = "-c \"import aiohttp\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null && proc.WaitForExit(5000) && proc.ExitCode == 0) {
                        return cmd;
                    }
                } catch { }
            }

            return "python3";
        }

        static IEnumerable<string> BundledBackendCandidates() {
            string baseDir = AppContext.BaseDirectory;
            yield return Path.Combine(baseDir, "Diffical", "backend");
            yield return Path.Combine(baseDir, "..", "Resources", "Diffical", "backend");
            yield return Path.Combine(baseDir, "..", "..", "Resources", "Diffical", "backend");
        }

        static string FindRustBackendBinary(string backendPath) {
            string dir = Directory.GetParent(backendPath)?.FullName ?? backendPath;
            string name = OperatingSystem.IsWindows() ? "diffical-backend.exe" : "diffical-backend";
            foreach (var candidate in new[] {
                Path.Combine(backendPath, name), Path.Combine(dir, name) }) {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        void StartRustServer(string binary, string backendPath) {
            string root = Directory.GetParent(backendPath)?.FullName ?? backendPath;
            string assembler = Environment.GetEnvironmentVariable("DIFFICAL_ASSEMBLER")
                ?? Path.Combine(root, "models", "assembler.onnx");
            string vocoder = Environment.GetEnvironmentVariable("DIFFICAL_VOCODER")
                ?? Path.Combine(root, "models", "vocoder.onnx");
            int port = endpoint.Port > 0 ? endpoint.Port : 8573;
            var psi = new ProcessStartInfo {
                FileName = binary,
                Arguments = $"serve --host 127.0.0.1 --port {port} --assembler \"{assembler}\" --vocoder \"{vocoder}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root,
            };
            try {
                serverProcess = Process.Start(psi);
                if (serverProcess != null) {
                    Log.Information($"Diffical Rust 后端已启动 (PID={serverProcess.Id})");
                    _ = Task.Run(() => {
                        try {
                            while (!serverProcess.StandardOutput.EndOfStream) {
                                var line = serverProcess.StandardOutput.ReadLine();
                                if (!string.IsNullOrEmpty(line)) Log.Information($"[Diffical] {line}");
                            }
                        } catch { }
                    });
                    _ = Task.Run(() => {
                        try {
                            while (!serverProcess.StandardError.EndOfStream) {
                                var line = serverProcess.StandardError.ReadLine();
                                if (!string.IsNullOrEmpty(line)) Log.Error($"[Diffical] {line}");
                            }
                        } catch { }
                    });
                }
            } catch (Exception e) {
                Log.Error(e, "启动 Diffical Rust 后端失败");
            }
        }

        /// <summary>
        /// 停止后端进程
        /// </summary>
        public void Stop() {
            if (serverProcess != null && !serverProcess.HasExited) {
                try {
                    serverProcess.Kill();
                    Log.Information("Diffical 后端进程已停止");
                } catch (Exception e) {
                    Log.Error(e, "停止 Diffical 后端失败");
                }
            }
        }
    }
}
