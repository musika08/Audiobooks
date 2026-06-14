using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace TTSApp
{
    // Pulls the latest release from GitHub and updates the app in place (via a relaunch script).
    public static class Updater
    {
        public const string AppVersion = "1.0.17";
        private const string LatestReleaseApi = "https://api.github.com/repos/musika08/Audiobooks/releases/latest";

        public static async Task CheckForUpdatesAsync()
        {
            string? tag, zipUrl;
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("TTSApp-Updater");
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                var json = await http.GetStringAsync(LatestReleaseApi);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                tag = root.GetProperty("tag_name").GetString();

                zipUrl = null;
                foreach (var asset in root.GetProperty("assets").EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zipUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not check for updates:\n{ex.Message}", "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(tag))
            {
                MessageBox.Show("No releases found.", "Update", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!IsNewer(tag, AppVersion))
            {
                MessageBox.Show($"You're up to date (v{AppVersion}).", "Update", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrEmpty(zipUrl))
            {
                MessageBox.Show($"A newer version ({tag}) exists but has no downloadable build attached.", "Update", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var go = MessageBox.Show(
                $"Update available: {tag} (you have v{AppVersion}).\n\nDownload and install now? The app will restart.",
                "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (go != MessageBoxResult.Yes) return;

            try
            {
                await DownloadAndApplyAsync(zipUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Update failed:\n{ex.Message}", "Update", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static async Task DownloadAndApplyAsync(string zipUrl)
        {
            string work = Path.Combine(Path.GetTempPath(), $"ttsapp_update_{Guid.NewGuid():N}");
            Directory.CreateDirectory(work);
            string zipPath = Path.Combine(work, "update.zip");
            string extractDir = Path.Combine(work, "extracted");

            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("TTSApp-Updater");
                using var resp = await http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(dst);
            }

            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            string appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            string exe = Path.Combine(appDir, "TTSApp.exe");

            int pid = Environment.ProcessId;

            // A batch script waits for THIS process to fully exit (so no DLLs are locked), copies the
            // new files over, relaunches, and self-deletes. Waiting on the PID prevents the partial
            // overwrite that corrupts the install (0xc0000005 on next launch).
            string bat = Path.Combine(work, "apply_update.bat");
            string script =
$@"@echo off
echo Updating AI Audiobook Studio. Please wait...
:waitloop
tasklist /fi ""PID eq {pid}"" 2>nul | find ""{pid}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto waitloop
)
timeout /t 1 /nobreak >nul
robocopy ""{extractDir}"" ""{appDir}"" /E /IS /IT /R:5 /W:2 /NFL /NDL /NJH /NJS /NP >nul
start """" ""{exe}""
rmdir /s /q ""{work}""
";
            File.WriteAllText(bat, script);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{bat}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            // Close the app so the script can overwrite its files.
            Application.Current.Shutdown();
        }

        private const string SidecarContentsApi =
            "https://api.github.com/repos/musika08/Audiobooks/contents/TTSApp/python?ref=main";

        // Live-pull the Python sidecar scripts (tts_server.py, requirements-*.txt, README) from main.
        // No release needed — these are interpreted, not compiled. Busts deps markers so changed
        // requirements reinstall on the next engine launch.
        public static async Task SyncSidecarFromGitHubAsync()
        {
            string pythonDir = Path.Combine(AppContext.BaseDirectory, "python");
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("TTSApp-Updater");
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                var json = await http.GetStringAsync(SidecarContentsApi);
                using var doc = JsonDocument.Parse(json);

                Directory.CreateDirectory(pythonDir);
                int count = 0;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.GetProperty("type").GetString() != "file") continue;
                    string name = item.GetProperty("name").GetString() ?? "";
                    if (!(name.EndsWith(".py") || name.EndsWith(".txt") || name.EndsWith(".md"))) continue;
                    string? dl = item.GetProperty("download_url").GetString();
                    if (string.IsNullOrEmpty(dl)) continue;

                    var bytes = await http.GetByteArrayAsync(dl);
                    await File.WriteAllBytesAsync(Path.Combine(pythonDir, name), bytes);
                    count++;
                }

                // Force engines to re-check dependencies (picks up changed requirements).
                foreach (var dir in Directory.GetDirectories(pythonDir, ".venv-*"))
                {
                    var marker = Path.Combine(dir, ".deps_ok");
                    try { if (File.Exists(marker)) File.Delete(marker); } catch { }
                }

                MessageBox.Show(
                    $"Synced {count} sidecar file(s) from GitHub.\n\nGPU engines will re-check their Python dependencies the next time you select one.",
                    "Sidecar Sync", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Sidecar sync failed:\n{ex.Message}", "Sidecar Sync", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // True if remote tag (e.g. "v1.2.0") is a higher version than current.
        private static bool IsNewer(string remoteTag, string current)
        {
            string r = remoteTag.TrimStart('v', 'V');
            if (Version.TryParse(r, out var rv) && Version.TryParse(current, out var cv))
                return rv > cv;
            // Fallback: different string ⇒ treat as newer.
            return !string.Equals(r, current, StringComparison.OrdinalIgnoreCase);
        }
    }
}
