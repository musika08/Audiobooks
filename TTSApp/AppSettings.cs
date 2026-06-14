using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TTSApp
{
    public static class AppSettings
    {
        private static readonly string SettingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TTSApp");
        private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");
        private static readonly string DictPath = Path.Combine(SettingsDir, "dictionary.json");

        public static int RewindSeconds { get; set; } = 10;
        public static int ForwardSeconds { get; set; } = 10;
        public static bool AnnounceChapterTitle { get; set; } = true;
        public static string Theme { get; set; } = "Dark";
        public static string SelectedModel { get; set; } = "kokoro-multi-lang-v1_0";
        public static bool EnableDialogMode { get; set; } = false;
        public static int DialogVoiceId { get; set; } = 1;

        // Voice Cast: narrator and dialogue rendered by separately-chosen engines/voices.
        // Allowed combos (enforced by VoiceCastWindow): Kokoro+Kokoro, Kokoro+oneGPU, or sameGPU+sameGPU.
        // Two different GPU engines is rejected (would need two models in VRAM).
        public static bool CastEnabled { get; set; } = false;
        public static string CastNarratorModel { get; set; } = "kokoro-multi-lang-v1_0";
        public static int CastNarratorVoiceId { get; set; } = 0;
        public static string? CastNarratorCloneRef { get; set; }
        public static string CastDialogueModel { get; set; } = "kokoro-multi-lang-v1_0";
        public static int CastDialogueVoiceId { get; set; } = 1;
        public static string? CastDialogueCloneRef { get; set; }
        public static int PauseAfterCommaMs { get; set; } = 50;
        public static int PauseAfterSentenceMs { get; set; } = 100;
        public static int PauseAfterParagraphMs { get; set; } = 500;
        // Ellipsis ("..." / "…") pause — longer than a period by default.
        public static int PauseAfterEllipsisMs { get; set; } = 200;
        // Global multiplier applied to ALL pauses at once (100 = normal, 200 = double, 0 = none).
        public static int PauseScalePercent { get; set; } = 100;
        // Level each spoken segment to equal loudness (keeps narrator and dialogue voices balanced).
        public static bool LevelSegmentVolume { get; set; } = true;
        public static bool MergeIntoSingleFile { get; set; } = false;
        public static string? LastProjectPath { get; set; }
        public static string? LastOutputDir { get; set; }
        public static string? CoverImagePath { get; set; }
        public static bool NormalizeAudio { get; set; } = false;
        // 0=Off, 1=Peak, 2=RMS, 3=LUFS
        public static int NormalizationMode { get; set; } = 0;
        public static double TargetLufs { get; set; } = -20;
        public static bool TrimSilence { get; set; } = false;
        public static string ExportPreset { get; set; } = "Custom";
        // Optional intro/outro clips and a background music bed (all off when empty). Requires ffmpeg.
        public static string? IntroAudioPath { get; set; }
        public static string? OutroAudioPath { get; set; }
        public static string? BackgroundAudioPath { get; set; }
        public static int BackgroundVolumePercent { get; set; } = 15;
        // Reference audio for voice cloning (GPU sidecar engines only). Runtime-only, not persisted.
        public static string? CloneReferencePath { get; set; }
        // Run a de-reverb/denoise pass on GPU-engine output (reduces room echo from reference clips).
        public static bool DereverbCloned { get; set; } = false;

        // GPU-engine voice tuning (each engine uses the ones relevant to it).
        public static double VoiceTemperature { get; set; } = 0.7;       // XTTS + Chatterbox
        public static double VoiceRepetitionPenalty { get; set; } = 2.0;  // XTTS
        public static double VoiceExaggeration { get; set; } = 0.5;       // Chatterbox
        public static double VoiceCfgWeight { get; set; } = 0.5;          // Chatterbox
        public static double VoiceCfgScale { get; set; } = 1.3;           // VibeVoice
        // Named, reusable cloned-voice reference clips (copied into the app's voices folder).
        public static List<SavedVoice> SavedVoices { get; set; } = new();

        // Window placement (restored on launch). 0/NaN sentinels = use defaults.
        public static double WindowWidth { get; set; } = 0;
        public static double WindowHeight { get; set; } = 0;
        public static double WindowLeft { get; set; } = double.NaN;
        public static double WindowTop { get; set; } = double.NaN;
        public static bool WindowMaximized { get; set; } = false;
        // One-time onboarding tips shown on first launch.
        public static bool FirstRunDone { get; set; } = false;
        public static string VoicesDir => Path.Combine(SettingsDir, "voices");
        public static Dictionary<string, string> PronunciationDict { get; set; } = new();

        public static void Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var data = JsonSerializer.Deserialize<SettingsData>(json);
                    if (data != null)
                    {
                        RewindSeconds = data.RewindSeconds;
                        ForwardSeconds = data.ForwardSeconds;
                        AnnounceChapterTitle = data.AnnounceChapterTitle;
                        Theme = data.Theme ?? "Dark";
                        SelectedModel = data.SelectedModel ?? "kokoro-multi-lang-v1_0";
                        EnableDialogMode = data.EnableDialogMode;
                        DialogVoiceId = data.DialogVoiceId;
                        CastEnabled = data.CastEnabled;
                        CastNarratorModel = data.CastNarratorModel ?? "kokoro-multi-lang-v1_0";
                        CastNarratorVoiceId = data.CastNarratorVoiceId;
                        CastNarratorCloneRef = data.CastNarratorCloneRef;
                        CastDialogueModel = data.CastDialogueModel ?? "kokoro-multi-lang-v1_0";
                        CastDialogueVoiceId = data.CastDialogueVoiceId;
                        CastDialogueCloneRef = data.CastDialogueCloneRef;
                        PauseAfterCommaMs = data.PauseAfterCommaMs;
                        PauseAfterSentenceMs = data.PauseAfterSentenceMs;
                        PauseAfterParagraphMs = data.PauseAfterParagraphMs;
                        PauseAfterEllipsisMs = data.PauseAfterEllipsisMs;
                        PauseScalePercent = data.PauseScalePercent;
                        LevelSegmentVolume = data.LevelSegmentVolume;
                        DereverbCloned = data.DereverbCloned;
                        VoiceTemperature = data.VoiceTemperature;
                        VoiceRepetitionPenalty = data.VoiceRepetitionPenalty;
                        VoiceExaggeration = data.VoiceExaggeration;
                        VoiceCfgWeight = data.VoiceCfgWeight;
                        VoiceCfgScale = data.VoiceCfgScale;
                        SavedVoices = data.SavedVoices ?? new();
                        MergeIntoSingleFile = data.MergeIntoSingleFile;
                        LastProjectPath = data.LastProjectPath;
                        LastOutputDir = data.LastOutputDir;
                        CoverImagePath = data.CoverImagePath;
                        NormalizeAudio = data.NormalizeAudio;
                        NormalizationMode = data.NormalizationMode;
                        TargetLufs = data.TargetLufs;
                        TrimSilence = data.TrimSilence;
                        ExportPreset = data.ExportPreset ?? "Custom";
                        IntroAudioPath = data.IntroAudioPath;
                        OutroAudioPath = data.OutroAudioPath;
                        BackgroundAudioPath = data.BackgroundAudioPath;
                        BackgroundVolumePercent = data.BackgroundVolumePercent;
                        WindowWidth = data.WindowWidth;
                        WindowHeight = data.WindowHeight;
                        WindowLeft = data.WindowLeft;
                        WindowTop = data.WindowTop;
                        WindowMaximized = data.WindowMaximized;
                        FirstRunDone = data.FirstRunDone;
                    }
                }
                if (File.Exists(DictPath))
                {
                    var json = File.ReadAllText(DictPath);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (dict != null) PronunciationDict = dict;
                }
            }
            catch { /* ignore load errors */ }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var data = new SettingsData
                {
                    RewindSeconds = RewindSeconds,
                    ForwardSeconds = ForwardSeconds,
                    AnnounceChapterTitle = AnnounceChapterTitle,
                    Theme = Theme,
                    SelectedModel = SelectedModel,
                    EnableDialogMode = EnableDialogMode,
                    DialogVoiceId = DialogVoiceId,
                    CastEnabled = CastEnabled,
                    CastNarratorModel = CastNarratorModel,
                    CastNarratorVoiceId = CastNarratorVoiceId,
                    CastNarratorCloneRef = CastNarratorCloneRef,
                    CastDialogueModel = CastDialogueModel,
                    CastDialogueVoiceId = CastDialogueVoiceId,
                    CastDialogueCloneRef = CastDialogueCloneRef,
                    PauseAfterCommaMs = PauseAfterCommaMs,
                    PauseAfterSentenceMs = PauseAfterSentenceMs,
                    PauseAfterParagraphMs = PauseAfterParagraphMs,
                    PauseAfterEllipsisMs = PauseAfterEllipsisMs,
                    PauseScalePercent = PauseScalePercent,
                    LevelSegmentVolume = LevelSegmentVolume,
                    DereverbCloned = DereverbCloned,
                    VoiceTemperature = VoiceTemperature,
                    VoiceRepetitionPenalty = VoiceRepetitionPenalty,
                    VoiceExaggeration = VoiceExaggeration,
                    VoiceCfgWeight = VoiceCfgWeight,
                    VoiceCfgScale = VoiceCfgScale,
                    SavedVoices = SavedVoices,
                    MergeIntoSingleFile = MergeIntoSingleFile,
                    LastProjectPath = LastProjectPath,
                    LastOutputDir = LastOutputDir,
                    CoverImagePath = CoverImagePath,
                    NormalizeAudio = NormalizeAudio,
                    NormalizationMode = NormalizationMode,
                    TargetLufs = TargetLufs,
                    TrimSilence = TrimSilence,
                    ExportPreset = ExportPreset,
                    IntroAudioPath = IntroAudioPath,
                    OutroAudioPath = OutroAudioPath,
                    BackgroundAudioPath = BackgroundAudioPath,
                    BackgroundVolumePercent = BackgroundVolumePercent,
                    WindowWidth = WindowWidth,
                    WindowHeight = WindowHeight,
                    WindowLeft = WindowLeft,
                    WindowTop = WindowTop,
                    WindowMaximized = WindowMaximized,
                    FirstRunDone = FirstRunDone
                };
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllText(DictPath, JsonSerializer.Serialize(PronunciationDict, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* ignore save errors */ }
        }

        public static string ApplyDictionary(string text)
        {
            if (PronunciationDict.Count == 0 || string.IsNullOrEmpty(text)) return text;
            foreach (var kv in PronunciationDict.OrderByDescending(kv => kv.Key.Length))
            {
                text = text.Replace(kv.Key, kv.Value);
            }
            return text;
        }

        public static string GetAutoSavePath() => Path.Combine(SettingsDir, "autosave.json");

        private class SettingsData
        {
            public int RewindSeconds { get; set; } = 10;
            public int ForwardSeconds { get; set; } = 10;
            public bool AnnounceChapterTitle { get; set; } = true;
            public string Theme { get; set; } = "Dark";
            public string SelectedModel { get; set; } = "kokoro-multi-lang-v1_0";
            public bool EnableDialogMode { get; set; } = false;
            public int DialogVoiceId { get; set; } = 1;
            public bool CastEnabled { get; set; } = false;
            public string? CastNarratorModel { get; set; } = "kokoro-multi-lang-v1_0";
            public int CastNarratorVoiceId { get; set; } = 0;
            public string? CastNarratorCloneRef { get; set; }
            public string? CastDialogueModel { get; set; } = "kokoro-multi-lang-v1_0";
            public int CastDialogueVoiceId { get; set; } = 1;
            public string? CastDialogueCloneRef { get; set; }
            public int PauseAfterCommaMs { get; set; } = 50;
            public int PauseAfterSentenceMs { get; set; } = 100;
            public int PauseAfterParagraphMs { get; set; } = 500;
            public int PauseAfterEllipsisMs { get; set; } = 200;
            public int PauseScalePercent { get; set; } = 100;
            public bool LevelSegmentVolume { get; set; } = true;
            public bool DereverbCloned { get; set; } = false;
            public double VoiceTemperature { get; set; } = 0.7;
            public double VoiceRepetitionPenalty { get; set; } = 2.0;
            public double VoiceExaggeration { get; set; } = 0.5;
            public double VoiceCfgWeight { get; set; } = 0.5;
            public double VoiceCfgScale { get; set; } = 1.3;
            public bool MergeIntoSingleFile { get; set; } = false;
            public string? LastProjectPath { get; set; }
            public string? LastOutputDir { get; set; }
            public string? CoverImagePath { get; set; }
            public bool NormalizeAudio { get; set; } = false;
            public int NormalizationMode { get; set; } = 0;
            public double TargetLufs { get; set; } = -20;
            public bool TrimSilence { get; set; } = false;
            public string ExportPreset { get; set; } = "Custom";
            public string? IntroAudioPath { get; set; }
            public string? OutroAudioPath { get; set; }
            public string? BackgroundAudioPath { get; set; }
            public int BackgroundVolumePercent { get; set; } = 15;
            public List<SavedVoice>? SavedVoices { get; set; }
            public double WindowWidth { get; set; } = 0;
            public double WindowHeight { get; set; } = 0;
            public double WindowLeft { get; set; } = double.NaN;
            public double WindowTop { get; set; } = double.NaN;
            public bool WindowMaximized { get; set; } = false;
            public bool FirstRunDone { get; set; } = false;
        }
    }

    public class SavedVoice
    {
        public string Name { get; set; } = "";
        public string FilePath { get; set; } = "";
    }
}
