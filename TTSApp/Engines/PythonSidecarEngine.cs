using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace TTSApp
{
    // GPU-only engine backed by a local Python FastAPI server (XTTS v2 in Phase 1).
    // The server process is shared/static so switching providers does not reload the model.
    public class PythonSidecarEngine : ITtsEngine
    {
        private const int Port = 8765;
        private static readonly string BaseUrl = $"http://127.0.0.1:{Port}";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

        private const string TorchIndexUrl = "https://download.pytorch.org/whl/cu121";

        private static Process? _serverProcess;
        private static string _runningModel = "";
        private static readonly object _lock = new();
        private static readonly StringBuilder _serverLog = new();

        // UI hooks: set by MainWindow. Invoked off the UI thread.
        public static Action<string>? StatusCallback;
        // null = indeterminate (working), 0..1 = real fraction.
        public static Action<double?>? ProgressCallback;

        private readonly string _modelName;
        private List<string> _speakers = new();
        private bool _initialized;

        public bool IsInitialized => _initialized;
        public string CurrentProvider { get; private set; } = "cuda";

        // Portable CPython (used only when no system Python is found). install_only build extracts to runtime\python\python.exe.
        private const string EmbeddedPythonUrl =
            "https://github.com/astral-sh/python-build-standalone/releases/download/20240814/cpython-3.11.9+20240814-x86_64-pc-windows-msvc-install_only.tar.gz";

        private static string ScriptDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "python");

        // Each engine gets its own venv so conflicting Python deps (e.g. transformers versions
        // for coqui-tts vs chatterbox-tts) never collide.
        private static string VenvDirFor(string model) => Path.Combine(ScriptDir, ".venv-" + model);
        private static string VenvPythonFor(string model) => Path.Combine(VenvDirFor(model), "Scripts", "python.exe");
        private static string DepsMarkerFor(string model) => Path.Combine(VenvDirFor(model), ".deps_ok");

        private string VenvDir => VenvDirFor(_modelName);
        private string VenvPython => VenvPythonFor(_modelName);
        private string DepsMarker => DepsMarkerFor(_modelName);
        private string EngineKey => _modelName switch { "chatterbox" => "chatterbox", "fish-opus" => "fish", _ => "xtts" };
        private string RequirementsFile => Path.Combine(ScriptDir, $"requirements-{EngineKey}.txt");

        private static string RuntimeDir => Path.Combine(ScriptDir, "runtime");
        private static string EmbeddedPython => Path.Combine(RuntimeDir, "python", "python.exe");
        private static string SetupLog => Path.Combine(ScriptDir, "setup.log");
        private static string PidFile => Path.Combine(ScriptDir, "server.pid");
        private static readonly object _logLock = new();

        public PythonSidecarEngine(string modelName)
        {
            _modelName = modelName;
        }

        public void Initialize(string provider)
        {
            CurrentProvider = provider;
            try { File.WriteAllText(SetupLog, $"=== {DateTime.Now} — initializing {_modelName} ==={Environment.NewLine}"); } catch { }
            Progress(null); // indeterminate "working" until/unless a step reports real %
            EnsureDependencies();
            EnsureServerRunning();
            WaitForHealth(hardCap: TimeSpan.FromMinutes(45), stallLimit: TimeSpan.FromMinutes(8));
            _speakers = FetchSpeakers();
            _initialized = true;
        }

        // True when this engine's venv/deps haven't been installed yet.
        public static bool NeedsFirstRunSetup(string modelName) =>
            !(File.Exists(VenvPythonFor(modelName)) && File.Exists(DepsMarkerFor(modelName)));

        private static void Report(string msg)
        {
            StatusCallback?.Invoke(msg);
            Log(msg);
        }
        private static void Progress(double? fraction) => ProgressCallback?.Invoke(fraction);

        // Append everything (status + raw pip/server lines) to python\setup.log for diagnosis.
        private static void Log(string line)
        {
            try
            {
                lock (_logLock)
                    File.AppendAllText(SetupLog, $"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
            }
            catch { /* logging must never throw */ }
        }

        public List<string> GetSpeakerNames() => _speakers;

        public void Generate(string text, int speakerId, float speed, string outputPath)
        {
            if (!_initialized) throw new InvalidOperationException("Sidecar engine is not initialized.");

            string speaker = speakerId >= 0 && speakerId < _speakers.Count ? _speakers[speakerId] : "";
            string speakerWav = AppSettings.CloneReferencePath ?? "";

            var payload = new
            {
                text,
                speaker,
                speaker_wav = speakerWav,
                speed,
                language = "en",
                denoise = AppSettings.DereverbCloned
            };
            var json = JsonSerializer.Serialize(payload);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = Http.PostAsync($"{BaseUrl}/synthesize", content).GetAwaiter().GetResult();

            if (!resp.IsSuccessStatusCode)
            {
                string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                throw new Exception($"Sidecar /synthesize failed: {(int)resp.StatusCode} {resp.ReasonPhrase}\n{body}");
            }

            var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            File.WriteAllBytes(outputPath, bytes);
        }

        // ----- server process management -----

        private void EnsureServerRunning()
        {
            lock (_lock)
            {
                // Reuse a running server only if it already hosts the requested model;
                // otherwise restart so a different GPU model is loaded into VRAM.
                if (_serverProcess is { HasExited: false } && _runningModel == _modelName) return;

                if (_serverProcess is { HasExited: false })
                {
                    try { _serverProcess.Kill(true); } catch { /* ignore */ }
                    _serverProcess = null;
                }

                string script = Path.Combine(ScriptDir, "tts_server.py");
                if (!File.Exists(script))
                    throw new FileNotFoundException($"Sidecar server script not found: {script}");

                var psi = new ProcessStartInfo
                {
                    FileName = VenvPython,
                    Arguments = $"\"{script}\" --port {Port} --model {_modelName}",
                    WorkingDirectory = ScriptDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

                // #24 — keep downloaded model weights inside the app folder so the install stays portable.
                string cacheDir = Path.Combine(ScriptDir, "cache");
                try { Directory.CreateDirectory(cacheDir); } catch { }
                psi.EnvironmentVariables["HF_HOME"] = cacheDir;
                psi.EnvironmentVariables["HUGGINGFACE_HUB_CACHE"] = Path.Combine(cacheDir, "hub");
                psi.EnvironmentVariables["TORCH_HOME"] = Path.Combine(cacheDir, "torch");
                psi.EnvironmentVariables["TTS_HOME"] = cacheDir; // Coqui XTTS

                try
                {
                    Report($"Starting {_modelName} server — loading model (first run downloads weights)...");
                    lock (_serverLog) { _serverLog.Clear(); }

                    _serverProcess = Process.Start(psi)
                        ?? throw new Exception("Failed to start python process (Process.Start returned null).");
                    _runningModel = _modelName;
                    try { File.WriteAllText(PidFile, _serverProcess.Id.ToString()); } catch { /* ignore */ }

                    void OnServerLine(string? line)
                    {
                        if (string.IsNullOrWhiteSpace(line)) return;
                        lock (_serverLog) { _serverLog.AppendLine(line); }
                        Log(line); // raw server/model-load line to setup.log
                        string shown = SummarizeServerLine(line);
                        if (shown.Length > 0) Report(shown);
                    }
                    _serverProcess.OutputDataReceived += (_, e) => OnServerLine(e.Data);
                    _serverProcess.ErrorDataReceived += (_, e) => OnServerLine(e.Data);
                    _serverProcess.BeginOutputReadLine();
                    _serverProcess.BeginErrorReadLine();
                }
                catch (Exception ex)
                {
                    throw new Exception($"Could not launch Python sidecar.\n{ex.Message}", ex);
                }
            }
        }

        // First-run setup: create a private venv and install torch (CUDA) + requirements into it.
        // Skipped once the marker file exists.
        private void EnsureDependencies()
        {
            if (File.Exists(VenvPython) && File.Exists(DepsMarker)) return;

            string requirements = RequirementsFile;
            if (!File.Exists(requirements))
                throw new FileNotFoundException($"Requirements file not found: {requirements}");

            if (!File.Exists(VenvPython))
            {
                Report($"Setting up Python environment for {_modelName} (one-time)...");
                string basePython = FindBasePython();
                RunStep(basePython, $"-m venv \"{VenvDir}\"", "Creating virtual environment");
            }

            RunStep(VenvPython, "-m pip install --upgrade pip", "Upgrading pip");
            Report("Installing PyTorch + torchaudio (CUDA 12.1) — large download, several minutes...");
            // torch and torchaudio from the SAME index so torchaudio's C extension matches torch.
            RunStep(VenvPython, $"-m pip install torch==2.4.1 torchaudio==2.4.1 --index-url {TorchIndexUrl}", "Installing PyTorch (CUDA)");
            Report($"Installing {_modelName} dependencies — this can take a while...");
            RunStep(VenvPython, $"-m pip install -r \"{requirements}\"", "Installing requirements");

            File.WriteAllText(DepsMarker, "cu121");
            Report("Python environment ready.");
        }

        // Locate a base interpreter to build the venv from. Prefers a system Python in the
        // 3.10–3.11 range (TTS deps don't build on 3.12+); otherwise uses the bundled 3.11.
        private static string FindBasePython()
        {
            // Prefer a 3.11 launcher explicitly, then any python, but reject 3.12+.
            foreach (var (file, args) in new[] { ("py", "-3.11"), ("py", "-3.10"), ("py", "-3"), ("python", "") })
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = file,
                        Arguments = $"{args} --version".Trim(),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var p = Process.Start(psi);
                    if (p == null) continue;
                    string verOut = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(10000);
                    if (p.ExitCode != 0) continue;

                    // Parse "Python 3.11.9" — only accept 3.10/3.11 (TTS packages fail to build on 3.12+).
                    var vm = Regex.Match(verOut, @"Python\s+3\.(\d+)");
                    if (vm.Success && int.TryParse(vm.Groups[1].Value, out int minor) && (minor == 10 || minor == 11))
                        return string.IsNullOrEmpty(args) ? file : $"{file}|{args}";
                }
                catch { /* try next */ }
            }
            // System Python missing or too new (3.12+) → use the bundled portable 3.11.
            Report("Using a bundled Python 3.11 (system Python is missing or too new for the TTS packages)...");
            return EnsureEmbeddedPython();
        }

        // Download + extract a private CPython when the machine has no Python.
        private static string EnsureEmbeddedPython()
        {
            if (File.Exists(EmbeddedPython)) return EmbeddedPython;

            Directory.CreateDirectory(RuntimeDir);
            string archive = Path.Combine(RuntimeDir, "python.tar.gz");

            try
            {
                Report("Python not found — downloading a private copy (~30 MB)...");
                using (var resp = Http.GetAsync(EmbeddedPythonUrl, HttpCompletionOption.ResponseHeadersRead)
                           .GetAwaiter().GetResult())
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? -1L;
                    using var src = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                    using var dst = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None);

                    var buffer = new byte[81920];
                    long read = 0;
                    int n;
                    while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        dst.Write(buffer, 0, n);
                        read += n;
                        if (total > 0) Progress((double)read / total);
                    }
                }

                Progress(null); // extraction has no easy %
                Report("Extracting Python...");
                using (var stream = File.OpenRead(archive))
                using (var reader = ReaderFactory.Open(stream))
                {
                    while (reader.MoveToNextEntry())
                    {
                        if (!reader.Entry.IsDirectory)
                            reader.WriteEntryToDirectory(RuntimeDir, new ExtractionOptions
                            {
                                ExtractFullPath = true,
                                Overwrite = true
                            });
                    }
                }
            }
            finally
            {
                try { File.Delete(archive); } catch { /* ignore */ }
            }

            if (!File.Exists(EmbeddedPython))
                throw new Exception($"Portable Python download/extract failed; expected {EmbeddedPython}.");

            return EmbeddedPython;
        }

        private static void RunStep(string fileSpec, string args, string what)
        {
            Report($"{what}...");

            // FindBasePython may return "py|-3"; split the launcher prefix back out.
            string file = fileSpec;
            string prefix = "";
            int bar = fileSpec.IndexOf('|');
            if (bar >= 0)
            {
                file = fileSpec[..bar];
                prefix = fileSpec[(bar + 1)..] + " ";
            }

            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = prefix + args,
                WorkingDirectory = ScriptDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // Force pip to print live, unbuffered, line-by-line so the status bar keeps moving.
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            psi.EnvironmentVariables["PIP_PROGRESS_BAR"] = "off";

            using var p = Process.Start(psi)
                ?? throw new Exception($"{what}: failed to start process '{file}'.");

            var log = new StringBuilder();
            void OnLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                lock (log) { log.AppendLine(line); }
                Log(line); // raw line to setup.log so you can see real progress
                string shown = SummarizePipLine(line);
                if (shown.Length > 0) Report($"{what} — {shown}");
            }

            p.OutputDataReceived += (_, e) => OnLine(e.Data);
            p.ErrorDataReceived += (_, e) => OnLine(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();

            if (p.ExitCode != 0)
            {
                string tail;
                lock (log) { tail = log.ToString().Trim(); }
                if (tail.Length > 1500) tail = tail[^1500..];
                throw new Exception($"{what} failed (exit {p.ExitCode}):\n{tail}");
            }
        }

        // Trim noisy pip/log lines down to something readable for the status bar.
        private static string SummarizePipLine(string line)
        {
            line = line.Trim();
            // Surface the meaningful pip phases; collapse everything else to a short form.
            string[] keep = { "Collecting", "Downloading", "Installing", "Building", "Preparing",
                              "Using cached", "Successfully", "Requirement already", "Resolving" };
            foreach (var k in keep)
                if (line.StartsWith(k, StringComparison.OrdinalIgnoreCase))
                    return line.Length > 90 ? line[..90] + "…" : line;

            // Otherwise ignore very chatty lines but keep anything short and informative.
            if (line.Length is > 0 and <= 90) return line;
            return "";
        }

        // Summarize server/model-load output (Coqui TTS + HuggingFace prints) for the status bar.
        private static string SummarizeServerLine(string line)
        {
            line = line.Trim();
            // Skip uvicorn/asyncio noise.
            if (line.StartsWith("INFO:") || line.StartsWith("WARNING:") || line.Contains("Uvicorn"))
                return "";

            string[] keep = { "Download", "download", "model", "Model", "Loading", "voices",
                              "%", "MB", "GB", "tts_models", "Generating", "Computing", "config" };
            foreach (var k in keep)
                if (line.Contains(k))
                    return line.Length > 90 ? "…" + line[^89..] : line;

            return "";
        }

        // Wait for the server to answer /health. Won't fail while the model is actively downloading
        // (cache still growing); only fails after `stallLimit` of no progress, or the `hardCap`.
        private void WaitForHealth(TimeSpan hardCap, TimeSpan stallLimit)
        {
            long expected = _modelName == "xtts-v2" ? 1_900_000_000L
                          : _modelName == "chatterbox" ? 1_200_000_000L
                          : 1_500_000_000L;

            // Coqui XTTS downloads to %LOCALAPPDATA%\tts; HF/torch caches go to python\cache. Watch both.
            string[] watchDirs =
            {
                Path.Combine(ScriptDir, "cache"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tts"),
            };

            var hardDeadline = DateTime.UtcNow + hardCap;
            long lastSize = -1;
            DateTime lastProgress = DateTime.UtcNow;
            bool everDownloaded = false;

            while (DateTime.UtcNow < hardDeadline)
            {
                if (_serverProcess is { HasExited: true })
                {
                    string err;
                    lock (_serverLog) { err = _serverLog.ToString().Trim(); }
                    if (err.Length > 1500) err = err[^1500..];
                    throw new Exception($"Sidecar server exited during startup.\n{err}");
                }
                try
                {
                    using var resp = Http.GetAsync($"{BaseUrl}/health").GetAwaiter().GetResult();
                    if (resp.IsSuccessStatusCode) return;
                }
                catch { /* not up yet */ }

                long size = 0;
                foreach (var d in watchDirs) size += DirSize(d);

                if (size > lastSize)
                {
                    // Download is progressing → reset the stall timer.
                    lastProgress = DateTime.UtcNow;
                    lastSize = size;
                    if (size > 0)
                    {
                        everDownloaded = true;
                        Report($"Downloading model — {size / 1_000_000.0:N0} MB...");
                        Progress(Math.Min(0.97, (double)size / expected));
                    }
                }
                else
                {
                    var idle = DateTime.UtcNow - lastProgress;
                    if (everDownloaded)
                    {
                        Report($"Model downloaded ({size / 1_000_000.0:N0} MB) — loading into the GPU (can take a minute)...");
                        Progress(null); // indeterminate during load so the bar isn't stuck at 97%
                    }
                    // Only give up if nothing has progressed for the whole stall window.
                    if (idle > stallLimit)
                        throw new TimeoutException(
                            "Sidecar server did not become healthy and has stopped making progress. " +
                            "Check python\\setup.log for details.");
                }

                Thread.Sleep(1000);
            }
            throw new TimeoutException("Sidecar server did not become healthy within the time limit.");
        }

        private static long DirSize(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return 0;
                long total = 0;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
                return total;
            }
            catch { return 0; }
        }

        private List<string> FetchSpeakers()
        {
            using var resp = Http.GetAsync($"{BaseUrl}/speakers").GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var list = new List<string>();
            foreach (var s in doc.RootElement.GetProperty("speakers").EnumerateArray())
                list.Add(s.GetString() ?? "");
            return list;
        }

        public static void ShutdownServer()
        {
            lock (_lock)
            {
                if (_serverProcess is { HasExited: false })
                {
                    try { _serverProcess.Kill(true); } catch { /* ignore */ }
                }
                _serverProcess = null;
                _runningModel = "";
                try { if (File.Exists(PidFile)) File.Delete(PidFile); } catch { /* ignore */ }
            }
        }

        // Kill a sidecar left running by a previous app instance that crashed without cleanup.
        public static void CleanupStaleServer()
        {
            try
            {
                if (!File.Exists(PidFile)) return;
                if (int.TryParse(File.ReadAllText(PidFile).Trim(), out int pid))
                {
                    try
                    {
                        var p = Process.GetProcessById(pid);
                        // Only kill if it's actually a python process (PID could have been reused).
                        if (p.ProcessName.Contains("python", StringComparison.OrdinalIgnoreCase))
                            p.Kill(true);
                    }
                    catch { /* process already gone */ }
                }
                File.Delete(PidFile);
            }
            catch { /* ignore */ }
        }

        public void Dispose()
        {
            // Engine instance goes away on provider switch, but the shared server stays alive
            // so the heavy model is not reloaded. App exit calls ShutdownServer().
            _initialized = false;
        }
    }
}
