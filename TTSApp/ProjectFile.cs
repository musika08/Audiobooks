using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TTSApp
{
    public class ProjectFile
    {
        public string Version { get; set; } = "1.0";
        public DateTime SavedAt { get; set; }
        public string? CoverImagePath { get; set; }
        public List<ChapterData> Chapters { get; set; } = new();
        public int SelectedVoiceIndex { get; set; }
        public float Speed { get; set; } = 1.0f;
        public int FormatIndex { get; set; } = 1;
        public int ProviderIndex { get; set; } = 0;
        public Dictionary<string, string> PronunciationDictSnapshot { get; set; } = new();

        public static void Save(string path, List<ChapterItem> chapters, int voiceIdx, float speed, int formatIdx, int providerIdx, string? coverPath)
        {
            var data = new ProjectFile
            {
                SavedAt = DateTime.Now,
                CoverImagePath = coverPath,
                SelectedVoiceIndex = voiceIdx,
                Speed = speed,
                FormatIndex = formatIdx,
                ProviderIndex = providerIdx,
                PronunciationDictSnapshot = new Dictionary<string, string>(AppSettings.PronunciationDict),
                Chapters = chapters.Select(c => new ChapterData
                {
                    Title = c.Title,
                    Content = c.Content,
                    IsSelected = c.IsSelected
                }).ToList()
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        public static ProjectFile? Load(string path)
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ProjectFile>(json);
        }

        public class ChapterData
        {
            public string Title { get; set; } = "";
            public string Content { get; set; } = "";
            public bool IsSelected { get; set; } = true;
        }
    }
}
