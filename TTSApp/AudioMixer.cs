using System;
using System.Diagnostics;
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
                // Loop the bed to cover the whole book, lower its volume, mix, end with the narration.
                string args = $"-y -i \"{current}\" -stream_loop -1 -i \"{bed}\" " +
                              $"-filter_complex \"[1:a]volume={vol.ToString(System.Globalization.CultureInfo.InvariantCulture)}[bed];" +
                              "[0:a][bed]amix=inputs=2:duration=first:dropout_transition=0[out]\" " +
                              "-map \"[out]\" -ac 1 \"" + mixed + "\"";
                if (Run(ffmpeg, args)) current = ReplaceIntermediate(current, bookWav, mixed);
                else { TryDelete(mixed); }
            }

            string? intro = AppSettings.IntroAudioPath;
            string? outro = AppSettings.OutroAudioPath;
            bool hasIntro = !string.IsNullOrEmpty(intro) && File.Exists(intro);
            bool hasOutro = !string.IsNullOrEmpty(outro) && File.Exists(outro);
            if (hasIntro || hasOutro)
            {
                string joined = NewTemp();
                var inputs = "";
                var labels = "";
                int n = 0;
                if (hasIntro) { inputs += $"-i \"{intro}\" "; labels += $"[{n}:a]"; n++; }
                inputs += $"-i \"{current}\" "; labels += $"[{n}:a]"; n++;
                if (hasOutro) { inputs += $"-i \"{outro}\" "; labels += $"[{n}:a]"; n++; }

                string args = $"-y {inputs}-filter_complex \"{labels}concat=n={n}:v=0:a=1[out]\" -map \"[out]\" -ac 1 \"{joined}\"";
                if (Run(ffmpeg, args)) current = ReplaceIntermediate(current, bookWav, joined);
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

        private static bool Run(string ffmpeg, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                p.OutputDataReceived += (_, _) => { };
                p.ErrorDataReceived += (_, _) => { };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(300000)) { try { p.Kill(true); } catch { } return false; }
                return p.ExitCode == 0;
            }
            catch { return false; }
        }
    }
}
