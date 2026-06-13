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

                var args = new StringBuilder();
                args.Append($"-y -i \"{inputWav}\" -i \"{metaPath}\" ");

                bool hasCover = !string.IsNullOrEmpty(coverImagePath) && File.Exists(coverImagePath);
                if (hasCover) args.Append($"-i \"{coverImagePath}\" ");

                args.Append("-map_metadata 1 ");
                args.Append("-map 0:a ");
                if (hasCover)
                {
                    args.Append("-map 2:v ");
                    args.Append("-c:v copy -disposition:v:0 attached_pic ");
                }
                args.Append(audioArgs).Append(' ');
                args.Append($"\"{output}\"");

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = args.ToString(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return false;
                // Drain stdout/stderr so ffmpeg's verbose output can't fill the pipe buffer and deadlock.
                proc.OutputDataReceived += (_, _) => { };
                proc.ErrorDataReceived += (_, _) => { };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                if (!proc.WaitForExit(300000)) // 5 min timeout
                {
                    try { proc.Kill(true); } catch { /* ignore */ }
                    return false;
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
                sb.AppendLine($"START={startMs}");
                sb.AppendLine($"END={endMs}");
                sb.AppendLine($"title={EscapeMetadata(chapters[i].Title)}");
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
