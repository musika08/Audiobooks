using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TTSApp.AI
{
    /// <summary>
    /// Orchestrates the optional AI text-assist feature: builds the configured provider, normalizes
    /// chapter text for speech (cached on disk), and scans text for hard-to-pronounce names. All
    /// failures fall back to the original text so a conversion is never blocked.
    /// </summary>
    public static class AiTextProcessor
    {
        private static string CacheDir => Path.Combine(AppSettings.SettingsDir, "ai-cache");

        // Local Ollama needs no key; hosted providers do.
        public static bool IsConfigured => AppSettings.AiEnabled && AppSettings.AiProvider switch
        {
            "ollama" => true,
            _ => !string.IsNullOrWhiteSpace(AppSettings.AiApiKey),
        };

        private static IAiProvider BuildProvider() => AppSettings.AiProvider switch
        {
            "deepseek" => new DeepSeekProvider(AppSettings.AiApiKey, AppSettings.AiModel),
            _ => new OllamaProvider(AppSettings.AiModel, AppSettings.AiBaseUrl),   // local-first default
        };

        // ---- Per-chapter text passes (normalize / skip-junk / heteronyms / emphasis) ----
        // Whatever passes are enabled are combined into ONE instruction, so it's one AI call per
        // chapter regardless of how many are on. Result cached by (flags + provider + model + text).

        private static string BuildChapterSystem()
        {
            var tasks = new System.Text.StringBuilder();
            if (AppSettings.AiNormalize)
                tasks.Append("- Expand abbreviations (Dr. -> Doctor, St. -> Street/Saint by context) and write numbers, dates, and times as spoken words (1987 -> nineteen eighty-seven). Remove page numbers, running headers/footers, and footnote markers; fix obvious OCR artifacts.\n");
            if (AppSettings.AiSkipJunk)
                tasks.Append("- Delete non-story boilerplate: translator's/editor's notes, advertisements, 'please rate/subscribe/donate' lines, site watermarks, and chapter navigation links. Keep the actual narrative intact.\n");
            if (AppSettings.AiHeteronyms)
                tasks.Append("- Where a context-dependent homograph would be mispronounced by a TTS voice, respell ONLY that word phonetically (e.g. 'lead the team' -> 'leed the team', 'a lead pipe' -> 'led pipe', 'he read it' -> 'he red it', 'a tear fell' -> 'a teer fell'). Leave all other words unchanged.\n");
            if (AppSettings.AiEmphasis)
                tasks.Append("- Insert pause tags written exactly as [pause 400] (the number is milliseconds) at strong dramatic beats and scene breaks to improve pacing. Use them sparingly (at most a few per chapter). Do not add any other markup.\n");

            return
                "You prepare prose to be read aloud by a text-to-speech narrator. Apply ONLY these edits:\n" +
                tasks +
                "Do NOT summarize, translate, censor, or otherwise change wording or meaning beyond the edits above. " +
                "Preserve paragraphs and dialogue. Return ONLY the edited text, with no preamble or commentary.";
        }

        public static async Task<string> ProcessChapterAsync(string text, CancellationToken token)
        {
            if (!IsConfigured || !AppSettings.AiAnyTextPass || string.IsNullOrWhiteSpace(text)) return text;

            string flags = $"{AppSettings.AiNormalize}{AppSettings.AiSkipJunk}{AppSettings.AiHeteronyms}{AppSettings.AiEmphasis}";
            string cacheKey = Hash($"chap|{flags}|{AppSettings.AiProvider}|{AppSettings.AiModel}|{text}");
            string cached = ReadCache(cacheKey);
            if (cached != null) return cached;

            try
            {
                string result = await BuildProvider().CompleteAsync(BuildChapterSystem(), text, token).ConfigureAwait(false);
                result = result.Trim();
                if (string.IsNullOrWhiteSpace(result)) return text;
                WriteCache(cacheKey, result);
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logging.Log.Error(ex, "AI chapter pass failed; using original text");
                return text;
            }
        }

        // ---- Name pronunciation scan ----

        private const string NamesSystem =
            "You are helping a text-to-speech narrator pronounce names correctly. From the user's text, find " +
            "proper names a US-English TTS voice would mispronounce: Chinese, Korean, Japanese, or other " +
            "non-English names, and hyphenated names (e.g. Seo-yeon, Min-jun). For each, give an English " +
            "phonetic respelling that an English voice will say correctly (e.g. Xiao -> Shaow, Zhang -> Jahng, " +
            "Seo-yeon -> Suh-yun). Ignore ordinary English names. Respond with ONLY a JSON array of objects " +
            "like [{\"name\":\"Seo-yeon\",\"say\":\"Suh-yun\"}], no other text. If there are none, return [].";

        /// <summary>
        /// Returns proposed name -> respelling pairs found in the text (deduplicated). Empty on failure.
        /// </summary>
        public static async Task<List<(string Name, string Say)>> ScanNamesAsync(string text, CancellationToken token)
        {
            var pairs = new List<(string, string)>();
            if (!IsConfigured || string.IsNullOrWhiteSpace(text)) return pairs;

            try
            {
                string raw = await BuildProvider().CompleteAsync(NamesSystem, text, token).ConfigureAwait(false);
                string json = ExtractJsonArray(raw);
                if (string.IsNullOrEmpty(json)) return pairs;

                using var doc = JsonDocument.Parse(json);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string name = el.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                    string say = el.TryGetProperty("say", out var s) ? (s.GetString() ?? "") : "";
                    name = name.Trim();
                    say = say.Trim();
                    if (name.Length == 0 || say.Length == 0 || name == say) continue;
                    if (seen.Add(name)) pairs.Add((name, say));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logging.Log.Error(ex, "AI name scan failed");
            }
            return pairs;
        }

        // ---- Chapter detection / titling ----

        private const string ChaptersSystem =
            "The user gives the text of a book that may lack clear chapter divisions. Insert a line " +
            "containing exactly '@@@CHAPTER@@@ <title>' immediately before the first line of each chapter, " +
            "where <title> is a short, sensible chapter title you derive from the content (e.g. " +
            "'@@@CHAPTER@@@ The Arrival'). Do NOT add, remove, translate, or reword any of the original " +
            "text — only insert those marker lines. Return the full text with markers inserted.";

        /// <summary>
        /// Re-splits a block of text into (title, content) chapters using the AI. Returns an empty list
        /// if the model found no divisions or on failure (caller keeps the existing chapters).
        /// </summary>
        public static async Task<List<(string Title, string Content)>> DetectChaptersAsync(string fullText, CancellationToken token)
        {
            var chapters = new List<(string, string)>();
            if (!IsConfigured || string.IsNullOrWhiteSpace(fullText)) return chapters;

            try
            {
                string marked = await BuildProvider().CompleteAsync(ChaptersSystem, fullText, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(marked) || !marked.Contains("@@@CHAPTER@@@")) return chapters;

                // Split on the marker; each part's first line is the title, the rest is the body.
                var parts = Regex.Split(marked, @"@@@CHAPTER@@@[ \t]*");
                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    if (trimmed.Length == 0) continue;
                    int nl = trimmed.IndexOf('\n');
                    string title = (nl < 0 ? trimmed : trimmed.Substring(0, nl)).Trim();
                    string body = (nl < 0 ? "" : trimmed.Substring(nl + 1)).Trim();
                    if (body.Length == 0) { body = title; title = $"Chapter {chapters.Count + 1}"; }
                    chapters.Add((title, body));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logging.Log.Error(ex, "AI chapter detection failed");
            }
            return chapters;
        }

        // ---- helpers ----

        // Models sometimes wrap JSON in ```json fences or add stray prose; grab the first [...] block.
        private static string ExtractJsonArray(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var m = Regex.Match(s, @"\[.*\]", RegexOptions.Singleline);
            return m.Success ? m.Value : "";
        }

        private static string Hash(string s)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
        }

        private static string ReadCache(string key)
        {
            try
            {
                string p = Path.Combine(CacheDir, key + ".txt");
                return File.Exists(p) ? File.ReadAllText(p) : null;
            }
            catch { return null; }
        }

        private static void WriteCache(string key, string value)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(Path.Combine(CacheDir, key + ".txt"), value);
            }
            catch (Exception ex) { Logging.Log.Error(ex, "Failed to write AI cache"); }
        }
    }
}
