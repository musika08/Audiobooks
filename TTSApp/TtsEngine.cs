using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NAudio.Lame;
using NAudio.Wave;
using SherpaOnnx;

namespace TTSApp
{
    public class TtsEngine : ITtsEngine
    {
        private OfflineTts? _tts;
        private bool _initialized = false;
        private readonly string _modelPath;
        private readonly string _modelName;
        private bool _levelSegments;

        public bool IsInitialized => _initialized;
        public string CurrentProvider { get; private set; } = "cpu";
        public int NumSpeakers => _tts?.NumSpeakers ?? 0;
        public int SampleRate => _tts?.SampleRate ?? 24000;

        private static readonly string[] KokoroEnSpeakerNames = new[]
        {
            "af", "af_bella", "af_nicole", "af_sarah", "af_sky",
            "am_adam", "am_michael", "bf_emma", "bf_isabella", "bm_george", "bm_lewis"
        };

        // Exact sid order for sherpa-onnx kokoro-multi-lang-v1_0 (53 speakers, includes am_santa/pm_santa).
        // Source: https://k2-fsa.github.io/sherpa/onnx/tts/all/Chinese-English/kokoro-multi-lang-v1_0.html
        private static readonly string[] KokoroMultiLangV1_0SpeakerNames = new[]
        {
            "af_alloy", "af_aoede", "af_bella", "af_heart", "af_jessica", "af_kore", "af_nicole", "af_nova", "af_river", "af_sarah", "af_sky",
            "am_adam", "am_echo", "am_eric", "am_fenrir", "am_liam", "am_michael", "am_onyx", "am_puck", "am_santa",
            "bf_alice", "bf_emma", "bf_isabella", "bf_lily",
            "bm_daniel", "bm_fable", "bm_george", "bm_lewis",
            "ef_dora", "em_alex", "ff_siwis",
            "hf_alpha", "hf_beta", "hm_omega", "hm_psi",
            "if_sara", "im_nicola",
            "jf_alpha", "jf_gongitsune", "jf_nezumi", "jf_tebukuro", "jm_kumo",
            "pf_dora", "pm_alex", "pm_santa",
            "zf_xiaobei", "zf_xiaoni", "zf_xiaoxiao", "zf_xiaoyi",
            "zm_yunjian", "zm_yunxi", "zm_yunxia", "zm_yunyang"
        };

        // Exact sid order for sherpa-onnx kokoro-multi-lang-v1_1 (v1.1-zh, 103 speakers).
        // Source: https://k2-fsa.github.io/sherpa/onnx/tts/all/Chinese-English/kokoro-multi-lang-v1_1.html
        private static readonly string[] KokoroMultiLangSpeakerNames = new[]
        {
            "af_maple", "af_sol", "bf_vale",
            "zf_001", "zf_002", "zf_003", "zf_004", "zf_005", "zf_006", "zf_007", "zf_008",
            "zf_017", "zf_018", "zf_019", "zf_021", "zf_022", "zf_023", "zf_024", "zf_026", "zf_027", "zf_028",
            "zf_032", "zf_036", "zf_038", "zf_039", "zf_040", "zf_042", "zf_043", "zf_044", "zf_046", "zf_047",
            "zf_048", "zf_049", "zf_051", "zf_059", "zf_060", "zf_067", "zf_070", "zf_071", "zf_072", "zf_073",
            "zf_074", "zf_075", "zf_076", "zf_077", "zf_078", "zf_079", "zf_083", "zf_084", "zf_085", "zf_086",
            "zf_087", "zf_088", "zf_090", "zf_092", "zf_093", "zf_094", "zf_099",
            "zm_009", "zm_010", "zm_011", "zm_012", "zm_013", "zm_014", "zm_015", "zm_016", "zm_020", "zm_025",
            "zm_029", "zm_030", "zm_031", "zm_033", "zm_034", "zm_035", "zm_037", "zm_041", "zm_045", "zm_050",
            "zm_052", "zm_053", "zm_054", "zm_055", "zm_056", "zm_057", "zm_058", "zm_061", "zm_062", "zm_063",
            "zm_064", "zm_065", "zm_066", "zm_068", "zm_069", "zm_080", "zm_081", "zm_082", "zm_089", "zm_091",
            "zm_095", "zm_096", "zm_097", "zm_098", "zm_100"
        };

        public List<string> GetSpeakerNames()
        {
            int count = NumSpeakers;
            var names = new List<string>();
            for (int i = 0; i < count; i++)
            {
                if (_modelName == "kokoro-en-v0_19" && i < KokoroEnSpeakerNames.Length)
                    names.Add(FriendlyVoiceName(KokoroEnSpeakerNames[i]));
                else if (_modelName == "kokoro-multi-lang-v1_0" && i < KokoroMultiLangV1_0SpeakerNames.Length)
                    names.Add(FriendlyVoiceName(KokoroMultiLangV1_0SpeakerNames[i]));
                else if (_modelName == "kokoro-multi-lang-v1_1" && i < KokoroMultiLangSpeakerNames.Length)
                    names.Add(FriendlyVoiceName(KokoroMultiLangSpeakerNames[i]));
                else
                    names.Add($"Speaker {i}");
            }
            return names;
        }

        private static readonly Dictionary<char, string> AccentMap = new()
        {
            ['a'] = "American", ['b'] = "British", ['e'] = "Spanish", ['f'] = "French",
            ['h'] = "Hindi", ['i'] = "Italian", ['j'] = "Japanese", ['p'] = "Brazilian", ['z'] = "Mandarin"
        };

        // Convert a Kokoro voice code (e.g. "am_adam", "af") into a readable label like "Adam — American Male"
        private static string FriendlyVoiceName(string code)
        {
            string prefix = code.Contains('_') ? code[..code.IndexOf('_')] : code;
            string accent = prefix.Length > 0 && AccentMap.TryGetValue(prefix[0], out var a) ? a : "Unknown";
            string gender = prefix.Length > 1 ? (prefix[1] == 'f' ? "Female" : prefix[1] == 'm' ? "Male" : "") : "";

            int us = code.IndexOf('_');
            string given = us >= 0 && us < code.Length - 1 ? code[(us + 1)..] : "Default Mix";
            given = char.ToUpperInvariant(given[0]) + given[1..];

            string descriptor = string.Join(" ", new[] { accent, gender }.Where(s => s.Length > 0));
            return $"{given} — {descriptor}  ({code})";
        }

        private string FindLexicon()
        {
            var candidates = new[] { "lexicon-us-en.txt", "lexicon.txt", "lexicon-en.txt" };
            foreach (var c in candidates)
            {
                var path = Path.Combine(_modelPath, c);
                if (File.Exists(path)) return path;
            }
            return "";
        }

        public TtsEngine()
        {
            _modelName = AppSettings.SelectedModel;
            _modelPath = Path.Combine(ModelDownloader.ModelDir, _modelName);
        }

        public void Initialize(string provider)
        {
            const int numThreads = 4;
            if (_initialized && CurrentProvider == provider) return;
            if (_initialized) Dispose();

            CurrentProvider = provider;

            var config = new OfflineTtsConfig
            {
                Model = new OfflineTtsModelConfig
                {
                    Provider = provider,
                    NumThreads = numThreads,
                    Debug = 0,
                    Kokoro = new OfflineTtsKokoroModelConfig
                    {
                        Model = Path.Combine(_modelPath, "model.onnx"),
                        Voices = Path.Combine(_modelPath, "voices.bin"),
                        Tokens = Path.Combine(_modelPath, "tokens.txt"),
                        DataDir = Path.Combine(_modelPath, "espeak-ng-data"),
                        DictDir = Directory.Exists(Path.Combine(_modelPath, "dict")) ? Path.Combine(_modelPath, "dict") : "",
                        Lexicon = FindLexicon(),
                        LengthScale = 1.0f
                    }
                }
            };

            _tts = new OfflineTts(config);
            _initialized = true;
        }

        public void Generate(string text, int speakerId, float speed, string outputPath)
        {
            if (_tts == null) throw new InvalidOperationException("TTS engine is not initialized.");

            text = NormalizeTextForTts(text);

            bool dialogMode = AppSettings.EnableDialogMode;
            int dialogVoiceId = AppSettings.DialogVoiceId;
            // Level each chunk to equal loudness only when mixing voices (dialog mode), so narrator
            // and dialogue match without flattening the dynamics of a single-voice chapter.
            _levelSegments = dialogMode && AppSettings.LevelSegmentVolume;

            // One global knob scales every pause type at once (100 = normal).
            double pauseScale = AppSettings.PauseScalePercent / 100.0;
            int commaPause = (int)(AppSettings.PauseAfterCommaMs * pauseScale);
            int sentencePause = (int)(AppSettings.PauseAfterSentenceMs * pauseScale);
            int paragraphPause = (int)(AppSettings.PauseAfterParagraphMs * pauseScale);
            int ellipsisPause = (int)(AppSettings.PauseAfterEllipsisMs * pauseScale);

            var tempFiles = new List<string>();

            if (dialogMode)
            {
                var segments = SplitIntoDialogSegments(text);
                foreach (var segment in segments)
                {
                    int sid = segment.IsDialog ? dialogVoiceId : speakerId;
                    RenderPiece(segment.Text, sid, speed, tempFiles, ellipsisPause);

                    if (paragraphPause > 0 && segment.Text.EndsWith("\n\n"))
                    {
                        AppendSilence(tempFiles, paragraphPause);
                    }
                }
            }
            else
            {
                // Split on inline [pause N] tags (N = milliseconds), then render each piece normally.
                var pieces = SplitOnPauseTags(text);
                foreach (var (pieceText, explicitPauseMs) in pieces)
                {
                    if (!string.IsNullOrWhiteSpace(pieceText))
                        RenderSentences(pieceText, speakerId, speed, tempFiles, commaPause, sentencePause, ellipsisPause);

                    if (explicitPauseMs > 0)
                        AppendSilence(tempFiles, (int)(explicitPauseMs * pauseScale));
                }
            }

            if (tempFiles.Count == 0)
                throw new Exception("No audio was generated. The text may be empty or invalid.");

            ConcatenateAndExport(tempFiles, outputPath);

            foreach (var f in tempFiles)
            {
                try { File.Delete(f); } catch { /* ignore */ }
            }
        }

        // Run the sentence → clause → ellipsis pipeline over a block of text.
        private void RenderSentences(string text, int speakerId, float speed, List<string> tempFiles,
            int commaPause, int sentencePause, int ellipsisPause)
        {
            var sentences = SplitIntoSentences(text);
            for (int i = 0; i < sentences.Count; i++)
            {
                var sentence = sentences[i].Trim();
                if (string.IsNullOrWhiteSpace(sentence)) continue;

                if (commaPause > 0)
                {
                    var clauses = SplitIntoClauses(sentence);
                    for (int j = 0; j < clauses.Count; j++)
                    {
                        RenderPiece(clauses[j], speakerId, speed, tempFiles, ellipsisPause);
                        if (j < clauses.Count - 1) AppendSilence(tempFiles, commaPause);
                    }
                }
                else
                {
                    RenderPiece(sentence, speakerId, speed, tempFiles, ellipsisPause);
                }

                if (sentencePause > 0 && i < sentences.Count - 1)
                    AppendSilence(tempFiles, sentencePause);
            }
        }

        // Parse "[pause 500]" tags → list of (text, explicitPauseMsAfter). The tags are removed from spoken text.
        private static List<(string Text, int PauseMs)> SplitOnPauseTags(string text)
        {
            var result = new List<(string, int)>();
            var rx = new Regex(@"\[pause\s+(\d+)\]", RegexOptions.IgnoreCase);
            int last = 0;
            foreach (Match m in rx.Matches(text))
            {
                string before = text.Substring(last, m.Index - last);
                int ms = int.TryParse(m.Groups[1].Value, out int v) ? v : 0;
                result.Add((before, ms));
                last = m.Index + m.Length;
            }
            if (last < text.Length) result.Add((text.Substring(last), 0));
            if (result.Count == 0) result.Add((text, 0));
            return result;
        }

        // Render a text piece, turning any ellipsis markers into real (scaled) pauses.
        // The marker is split out so the dots themselves never reach the model.
        private void RenderPiece(string text, int speakerId, float speed, List<string> tempFiles, int ellipsisPause)
        {
            var parts = text.Split(EllipsisMarker);
            for (int k = 0; k < parts.Length; k++)
            {
                var part = parts[k].Trim();
                if (part.Length > 0)
                    GenerateSegment(part, speakerId, speed, tempFiles);

                // A marker sat between part k and k+1 → insert the ellipsis pause there.
                if (k < parts.Length - 1 && ellipsisPause > 0)
                    AppendSilence(tempFiles, ellipsisPause);
            }
        }

        private void GenerateSegment(string text, int speakerId, float speed, List<string> tempFiles)
        {
            var audio = _tts!.Generate(text, speed, speakerId);
            if (audio == null) return;

            var tempFile = Path.Combine(Path.GetTempPath(), $"tts_chunk_{Guid.NewGuid()}.wav");
            audio.SaveToWaveFile(tempFile);
            audio.Dispose();

            // Level each chunk to a common loudness so different voices match (dialog mode).
            if (_levelSegments)
                AudioNormalizer.NormalizeLoudness(tempFile, targetRmsDbfs: -20f);

            tempFiles.Add(tempFile);
        }

        private void AppendSilence(List<string> tempFiles, int milliseconds)
        {
            var silenceFile = Path.Combine(Path.GetTempPath(), $"tts_silence_{Guid.NewGuid()}.wav");
            int sampleRate = SampleRate;
            int channels = 1;
            int bytesPerSample = 2;
            int numSamples = (int)(sampleRate * (milliseconds / 1000.0));
            int byteCount = numSamples * channels * bytesPerSample;

            var format = new WaveFormat(sampleRate, 16, channels);
            using (var writer = new WaveFileWriter(silenceFile, format))
            {
                var silence = new byte[byteCount];
                writer.Write(silence, 0, silence.Length);
            }
            tempFiles.Add(silenceFile);
        }

        // Internal placeholder for an ellipsis; never sent to the model, converted to a pause instead.
        private const char EllipsisMarker = '\u0001';

        private static string NormalizeTextForTts(string text)
        {
            text = text.Replace("\u2014", "-")
                       .Replace("\u201C", "\"").Replace("\u201D", "\"")
                       .Replace("\u2018", "'").Replace("\u2019", "'");

            // Treat semicolons and colons as full stops (sentence-tier pause + full-stop intonation).
            text = text.Replace(";", ".").Replace(":", ".");

            // Ellipsis ("\u2026" or runs of 2+ dots) \u2192 a sentinel we later turn into a real pause.
            // The dots are never sent to Kokoro (it emits static when fed multiple dots).
            text = text.Replace("\u2026", EllipsisMarker.ToString());
            text = Regex.Replace(text, @"\.{2,}", EllipsisMarker.ToString());

            text = Regex.Replace(text, @"([,.!?;:])([^ \t\n\r])", "$1 $2");
            text = Regex.Replace(text, @"[ \t]+", " ");

            return text;
        }

        private static List<string> SplitIntoSentences(string text)
        {
            var sentences = new List<string>();
            var matches = Regex.Matches(text, @"[^.!?]+[.!?]+");

            foreach (Match m in matches)
            {
                var s = m.Value.Trim();
                if (s.Length > 0) sentences.Add(s);
            }

            var lastMatch = matches.Count > 0 ? matches[matches.Count - 1] : null;
            if (lastMatch != null && lastMatch.Index + lastMatch.Length < text.Length)
            {
                var remainder = text.Substring(lastMatch.Index + lastMatch.Length).Trim();
                if (!string.IsNullOrWhiteSpace(remainder))
                    sentences.Add(remainder);
            }

            if (sentences.Count == 0 && !string.IsNullOrWhiteSpace(text))
                sentences.Add(text);

            return sentences;
        }

        // Split a sentence into clauses at commas, keeping the comma with each clause.
        private static List<string> SplitIntoClauses(string sentence)
        {
            var parts = Regex.Split(sentence, @"(?<=,)\s+");
            var clauses = new List<string>();
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0) clauses.Add(t);
            }
            if (clauses.Count == 0) clauses.Add(sentence);
            return clauses;
        }

        private class DialogSegment
        {
            public string Text { get; set; } = "";
            public bool IsDialog { get; set; }
        }

        private static List<DialogSegment> SplitIntoDialogSegments(string text)
        {
            var segments = new List<DialogSegment>();
            var pattern = "(\"[^\"]*\"|\u201C[^\u201D]*\u201D)";
            int lastIndex = 0;
            var matches = Regex.Matches(text, pattern);

            foreach (Match m in matches)
            {
                if (m.Index > lastIndex)
                {
                    var narration = text.Substring(lastIndex, m.Index - lastIndex);
                    if (!string.IsNullOrWhiteSpace(narration))
                        segments.Add(new DialogSegment { Text = narration.Trim(), IsDialog = false });
                }

                var dialog = m.Value.Trim('"', '\u201C', '\u201D');
                if (!string.IsNullOrWhiteSpace(dialog))
                    segments.Add(new DialogSegment { Text = dialog, IsDialog = true });

                lastIndex = m.Index + m.Length;
            }

            if (lastIndex < text.Length)
            {
                var remainder = text.Substring(lastIndex).Trim();
                if (!string.IsNullOrWhiteSpace(remainder))
                    segments.Add(new DialogSegment { Text = remainder, IsDialog = false });
            }

            if (segments.Count == 0)
                segments.Add(new DialogSegment { Text = text, IsDialog = false });

            return segments;
        }

        private static void ConcatenateAndExport(List<string> inputFiles, string outputPath)
        {
            var ext = Path.GetExtension(outputPath).ToLowerInvariant();
            var tempWav = ext == ".wav" ? outputPath : Path.ChangeExtension(outputPath, ".tmp.wav");

            using (var first = new WaveFileReader(inputFiles[0]))
            {
                using var writer = new WaveFileWriter(tempWav, first.WaveFormat);
                foreach (var file in inputFiles)
                {
                    using var reader = new WaveFileReader(file);
                    if (reader.WaveFormat.Encoding != first.WaveFormat.Encoding ||
                        reader.WaveFormat.SampleRate != first.WaveFormat.SampleRate ||
                        reader.WaveFormat.Channels != first.WaveFormat.Channels)
                    {
                        continue;
                    }
                    var buffer = new byte[8192];
                    int bytesRead;
                    while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        writer.Write(buffer, 0, bytesRead);
                    }
                }
            }

            if (ext == ".mp3")
            {
                try
                {
                    ConvertWavToMp3(tempWav, outputPath);
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

        private static void ConvertWavToMp3(string wavPath, string mp3Path)
        {
            using var reader = new WaveFileReader(wavPath);
            using var writer = new LameMP3FileWriter(mp3Path, reader.WaveFormat, 192);
            reader.CopyTo(writer);
        }

        public void Dispose()
        {
            _tts?.Dispose();
            _tts = null;
            _initialized = false;
        }
    }
}
