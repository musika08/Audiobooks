using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Lame;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TTSApp
{
    // Mixed-engine narrator/dialogue rendering. Routes quoted dialogue to one engine/voice and
    // narration to another, then resamples every segment to a common rate and concatenates.
    // Allowed engine combos are enforced by VoiceCastWindow (never two different GPU models).
    public static class VoiceCast
    {
        private const int TargetSampleRate = 24000;

        public sealed class Role
        {
            public ITtsEngine Engine = null!;
            public int SpeakerId;
            public string? CloneRef; // null = use the engine's built-in speaker
        }

        // Render one chapter: split into narrator/dialogue segments, synth each with its role's
        // engine/voice, then merge to outputPath (.wav or .mp3).
        public static void Render(string text, float speed, string outputPath, Role narrator, Role dialogue,
            CancellationToken token = default)
        {
            RenderAsync(text, speed, outputPath, narrator, dialogue, token).GetAwaiter().GetResult();
        }

        public static async Task RenderAsync(string text, float speed, string outputPath, Role narrator, Role dialogue,
            CancellationToken token = default)
        {
            var segments = TtsEngine.GetDialogSegments(text);
            var temps = new List<string>();

            // Each role renders single-voice; routing happens here, not inside an engine's dialog mode.
            string? savedClone = AppSettings.CloneReferencePath;
            try
            {
                foreach (var seg in segments)
                {
                    if (token.IsCancellationRequested) break;
                    if (string.IsNullOrWhiteSpace(seg.Text)) continue;
                    var role = seg.IsDialog ? dialogue : narrator;

                    string tmp = Path.Combine(Path.GetTempPath(), $"cast_{Guid.NewGuid():N}.wav");
                    AppSettings.CloneReferencePath = role.CloneRef; // ignored by Kokoro engines
                    await role.Engine.GenerateAsync(seg.Text, role.SpeakerId, speed, tmp, token).ConfigureAwait(false);

                    if (AppSettings.LevelSegmentVolume)
                    {
                        try { AudioNormalizer.NormalizeLoudness(tmp, targetRmsDbfs: -20f); }
                        catch (Exception ex) { Logging.Log.Error(ex, "Voice Cast segment leveling failed"); }
                    }
                    temps.Add(tmp);
                }
            }
            finally
            {
                AppSettings.CloneReferencePath = savedClone;
            }

            if (temps.Count == 0)
                throw new InvalidOperationException("Voice Cast produced no audio (the chapter text may be empty).");

            MergeResampleExport(temps, outputPath);

            foreach (var t in temps)
            {
                try { File.Delete(t); } catch (Exception ex) { Logging.Log.Error(ex, "Failed to delete Voice Cast temp file"); }
            }
        }

        // Resample every segment to a common mono 24 kHz 16-bit PCM stream so outputs from
        // different engines (and sample rates) concatenate cleanly.
        private static void MergeResampleExport(List<string> inputs, string outputPath)
        {
            var ext = Path.GetExtension(outputPath).ToLowerInvariant();
            string tempWav = ext == ".wav" ? outputPath : Path.ChangeExtension(outputPath, ".cast.tmp.wav");
            var outFormat = new WaveFormat(TargetSampleRate, 16, 1);

            using (var writer = new WaveFileWriter(tempWav, outFormat))
            {
                foreach (var file in inputs)
                {
                    using var reader = new AudioFileReader(file);
                    ISampleProvider sp = reader;
                    if (reader.WaveFormat.Channels > 1) sp = sp.ToMono();
                    if (reader.WaveFormat.SampleRate != TargetSampleRate)
                        sp = new WdlResamplingSampleProvider(sp, TargetSampleRate);

                    var buf = new float[TargetSampleRate];
                    int n;
                    while ((n = sp.Read(buf, 0, buf.Length)) > 0)
                        writer.WriteSamples(buf, 0, n);
                }
            }

            if (ext == ".mp3")
            {
                try
                {
                    using (var r = new WaveFileReader(tempWav))
                    using (var w = new LameMP3FileWriter(outputPath, r.WaveFormat, 192))
                        r.CopyTo(w);
                    File.Delete(tempWav);
                }
                catch (Exception ex)
                {
                    var wavOutput = Path.ChangeExtension(outputPath, ".wav");
                    File.Move(tempWav, wavOutput, true);
                    throw new Exception($"MP3 encoding failed: {ex.Message}. File saved as WAV instead.");
                }
            }
        }
    }
}
