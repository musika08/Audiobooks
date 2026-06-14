using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace TTSApp
{
    public static class M4BHelper
    {
        /// <summary>
        /// Creates a proper M4B audiobook with embedded chapters using ffmpeg.
        /// </summary>
        public static bool CreateM4B(string mergedWavPath, string outputM4bPath,
            List<(string Title, TimeSpan Start)> chapters, string? coverImagePath)
            => Encode(mergedWavPath, outputM4bPath, chapters, coverImagePath, "-c:a aac -b:a 192k");

        /// <summary>
        /// Creates an MP3 with embedded ID3v2 chapter marks (via ffmpeg). Returns false if ffmpeg is missing.
        /// </summary>
        public static bool CreateMp3WithChapters(string mergedWavPath, string outputMp3Path,
            List<(string Title, TimeSpan Start)> chapters, string? coverImagePath)
            => Encode(mergedWavPath, outputMp3Path, chapters, coverImagePath, "-c:a libmp3lame -b:a 192k -id3v2_version 3 -write_id3v1 1");

        private static bool Encode(string inputWav, string output,
            List<(string Title, TimeSpan Start)> chapters, string? coverImagePath, string audioArgs)
        {
            string? ffmpeg = FindFfmpeg();
            if (string.IsNullOrEmpty(ffmpeg)) return false;

            string metaPath = Path.Combine(Path.GetTempPath(), $"ffmeta_{Guid.NewGuid()}.txt");
            try
            {
                WriteFfmetadata(metaPath, chapters);

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // Build arguments using ArgumentList so paths with quotes/spaces cannot break out.
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(inputWav);
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(metaPath);

                bool hasCover = !string.IsNullOrEmpty(coverImagePath) && File.Exists(coverImagePath);
                if (hasCover)
                {
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add(coverImagePath!);
                }

                psi.ArgumentList.Add("-map_metadata");
                psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("0:a");
                if (hasCover)
                {
                    psi.ArgumentList.Add("-map");
                    psi.ArgumentList.Add("2:v");
                    psi.ArgumentList.Add("-c:v");
                    psi.ArgumentList.Add("copy");
                    psi.ArgumentList.Add("-disposition:v:0");
                    psi.ArgumentList.Add("attached_pic");
                }

                foreach (var part in audioArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    psi.ArgumentList.Add(part);

                psi.ArgumentList.Add(output);

                using var proc = Process.Start(psi);
                if (proc == null) return false;

                var stderr = new StringBuilder();
                proc.OutputDataReceived += (_, _) => { };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data)) stderr.AppendLine(e.Data);
                };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                if (!proc.WaitForExit(300000)) // 5 min timeout
                {
                    try { proc.Kill(true); } catch { /* ignore */ }
                    Logging.Log.Error($"ffmpeg timed out encoding {output}. Stderr:\n{stderr}");
                    return false;
                }

                if (proc.ExitCode != 0)
                {
                    Logging.Log.Error($"ffmpeg failed (exit {proc.ExitCode}) encoding {output}. Stderr:\n{stderr}");
                }

                return proc.ExitCode == 0 && File.Exists(output);
            }
            finally
            {
                try { File.Delete(metaPath); } catch { }
            }
        }

        private static void WriteFfmetadata(string path, List<(string Title, TimeSpan Start)> chapters)
        {
            var sb = new StringBuilder();
            sb.AppendLine(";FFMETADATA1");
            sb.AppendLine("title=Audiobook");
            sb.AppendLine("artist=AI Audiobook");
            sb.AppendLine();

            for (int i = 0; i < chapters.Count; i++)
            {
                long startMs = (long)chapters[i].Start.TotalMilliseconds;
                long endMs = (i < chapters.Count - 1)
                    ? (long)chapters[i + 1].Start.TotalMilliseconds
                    : startMs + 60000; // fallback: add 1 min for last chapter; ffmpeg will stretch

                sb.AppendLine("[CHAPTER]");
                sb.AppendLine("TIMEBASE=1/1000");
                sb.AppendLine("START=" + startMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
                sb.AppendLine("END=" + endMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
                sb.AppendLine("title=" + EscapeMetadata(chapters[i].Title));
                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static string EscapeMetadata(string value)
        {
            return value.Replace("\\", "\\\\").Replace("=", "\\=").Replace(";", "\\;").Replace("#", "\\#").Replace("\n", " ");
        }

        public static string? FindFfmpeg()
        {
            var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
            foreach (var p in paths)
            {
                var full = Path.Combine(p, "ffmpeg.exe");
                if (File.Exists(full)) return full;
            }
            return null;
        }
    }
}
