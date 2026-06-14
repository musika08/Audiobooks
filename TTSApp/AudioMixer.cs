using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace TTSApp
{
    // #14 — optional background music bed + intro/outro clips, applied to the merged book WAV.
    // All steps require ffmpeg; if it's missing or no assets are set, the input is returned unchanged.
    public static class AudioMixer
    {
        /// <summary>
        /// Applies background bed and/or intro/outro to a book WAV. Returns the path to use
        /// (a new temp file, or the original if nothing was applied). Caller owns deleting the result.
        /// </summary>
        public static string Apply(string bookWav)
        {
            string? ffmpeg = M4BHelper.FindFfmpeg();
            if (string.IsNullOrEmpty(ffmpeg)) return bookWav;

            string current = bookWav;

            string? bed = AppSettings.BackgroundAudioPath;
            if (!string.IsNullOrEmpty(bed) && File.Exists(bed))
            {
                string mixed = NewTemp();
                double vol = Math.Clamp(AppSettings.BackgroundVolumePercent, 0, 100) / 100.0;

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // Loop the bed to cover the whole book, lower its volume, mix, end with the narration.
                string filter = $"[1:a]volume={vol.ToString(CultureInfo.InvariantCulture)}[bed];[0:a][bed]amix=inputs=2:duration=first:dropout_transition=0[out]";
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(current);
                psi.ArgumentList.Add("-stream_loop");
                psi.ArgumentList.Add("-1");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(bed);
                psi.ArgumentList.Add("-filter_complex");
                psi.ArgumentList.Add(filter);
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("[out]");
                psi.ArgumentList.Add("-ac");
                psi.ArgumentList.Add("1");
                psi.ArgumentList.Add(mixed);

                if (Run(psi, mixed)) current = ReplaceIntermediate(current, bookWav, mixed);
                else { TryDelete(mixed); }
            }

            string? intro = AppSettings.IntroAudioPath;
            string? outro = AppSettings.OutroAudioPath;
            bool hasIntro = !string.IsNullOrEmpty(intro) && File.Exists(intro);
            bool hasOutro = !string.IsNullOrEmpty(outro) && File.Exists(outro);
            if (hasIntro || hasOutro)
            {
                string joined = NewTemp();
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                int n = 0;
                if (hasIntro) { psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(intro!); n++; }
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(current); n++;
                if (hasOutro) { psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(outro!); n++; }

                var labels = new System.Text.StringBuilder();
                for (int i = 0; i < n; i++)
                    labels.Append(CultureInfo.InvariantCulture, $"[{i}:a]");
                string filter = $"{labels}concat=n={n}:v=0:a=1[out]";
                psi.ArgumentList.Add("-filter_complex");
                psi.ArgumentList.Add(filter);
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("[out]");
                psi.ArgumentList.Add("-ac");
                psi.ArgumentList.Add("1");
                psi.ArgumentList.Add(joined);

                if (Run(psi, joined)) current = ReplaceIntermediate(current, bookWav, joined);
                else { TryDelete(joined); }
            }

            return current;
        }

        private static string ReplaceIntermediate(string current, string original, string next)
        {
            if (current != original) TryDelete(current); // delete the previous temp, keep originals
            return next;
        }

        private static string NewTemp() => Path.Combine(Path.GetTempPath(), $"mix_{Guid.NewGuid()}.wav");

        private static void TryDelete(string p) { try { File.Delete(p); } catch { } }

        private static bool Run(ProcessStartInfo psi, string outputHint)
        {
            try
            {
                using var p = Process.Start(psi);
                if (p == null) return false;
                var stderr = new System.Text.StringBuilder();
                p.OutputDataReceived += (_, _) => { };
                p.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data)) stderr.AppendLine(e.Data);
                };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(300000)) { try { p.Kill(true); } catch { } return false; }
                if (p.ExitCode != 0)
                {
                    Logging.Log.Error($"ffmpeg mix failed (exit {p.ExitCode}) for {outputHint}. Stderr:\n{stderr}");
                }
                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Logging.Log.Error(ex, $"ffmpeg mix failed for {outputHint}");
                return false;
            }
        }
    }
}
