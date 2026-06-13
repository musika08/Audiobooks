using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TTSApp
{
    public static class TextCleaner
    {
        /// <summary>
        /// Tidies chapter text: removes stray spaces, reflows mid-sentence line breaks,
        /// and separates paragraphs (blank line) where a sentence ends. Safe to run repeatedly.
        /// </summary>
        public static string TidyContent(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? "";

            const string Para = "@@PARA_BREAK@@"; // transient paragraph marker
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            text = text.Replace(((char)0x00A0).ToString(), " "); // non-breaking spaces -> normal spaces

            // Decide how single line breaks are used in this text.
            bool hasBlankLineParagraphs = Regex.IsMatch(text, @"\n[ \t]*\n");

            if (hasBlankLineParagraphs)
            {
                // Blank lines already separate paragraphs → treat single line breaks as wraps and
                // reflow them into flowing, book-like paragraphs.
                text = Regex.Replace(text, @"\n[ \t]*\n+", Para); // protect paragraph breaks
                text = text.Replace("\n", " ");                   // join wrapped lines
                text = text.Replace(Para, "\n\n");                // restore paragraph breaks
            }
            else
            {
                // No blank lines: each line break IS a paragraph break (don't merge distinct lines).
                text = text.Replace("\n", "\n\n");
            }

            // Whitespace cleanup.
            text = Regex.Replace(text, @"[ \t]+", " ");
            text = Regex.Replace(text, @" +([,.!?;:])", "$1"); // no space before punctuation
            text = Regex.Replace(text, @"\n{3,}", "\n\n");

            var paragraphs = text.Split(new[] { "\n\n" }, StringSplitOptions.None)
                                 .Select(p => p.Trim())
                                 .Where(p => p.Length > 0);
            return string.Join("\n\n", paragraphs);
        }

        /// <summary>
        /// Cleans up PDF-extracted text by removing repeated headers/footers,
        /// page numbers, and fixing hyphenated words broken across lines.
        /// </summary>
        public static List<string> CleanPdfPages(List<string> pages)
        {
            if (pages == null || pages.Count < 2)
                return pages ?? new List<string>();

            // Detect repeated first lines (likely headers)
            var firstLines = pages
                .Select(p => p.Split('\n').FirstOrDefault()?.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .ToList();
            var headers = firstLines
                .GroupBy(x => x)
                .Where(g => g.Count() > pages.Count / 2)
                .Select(g => g.Key)
                .ToHashSet();

            // Detect repeated last lines (likely footers)
            var lastLines = pages
                .Select(p => p.Split('\n').LastOrDefault()?.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .ToList();
            var footers = lastLines
                .GroupBy(x => x)
                .Where(g => g.Count() > pages.Count / 2)
                .Select(g => g.Key)
                .ToHashSet();

            var cleaned = new List<string>();
            foreach (var page in pages)
            {
                var lines = page.Split('\n').ToList();

                // Remove header
                if (lines.Count > 0 && headers.Contains(lines[0].Trim()))
                    lines.RemoveAt(0);

                // Remove footer
                if (lines.Count > 0 && footers.Contains(lines[^1].Trim()))
                    lines.RemoveAt(lines.Count - 1);

                // Remove standalone page numbers from what is now the last line
                if (lines.Count > 0)
                {
                    var last = lines[^1].Trim();
                    if (Regex.IsMatch(last, @"^(\d+|[Pp]age\s*\d+)$"))
                        lines.RemoveAt(lines.Count - 1);
                }

                cleaned.Add(string.Join("\n", lines));
            }

            // Fix hyphenation across the entire document
            var merged = string.Join("\n\n", cleaned);
            merged = FixHyphenation(merged);

            // Final whitespace normalization
            merged = Regex.Replace(merged, @"[ \t]+", " ");
            merged = Regex.Replace(merged, @"\n\s*\n", "\n\n");
            merged = Regex.Replace(merged, @"\n{3,}", "\n\n");

            return new List<string> { merged };
        }

        /// <summary>
        /// Removes line-break hyphens: "creat-\nure" becomes "creature".
        /// Only fixes when both sides are alphabetic.
        /// </summary>
        private static string FixHyphenation(string text)
        {
            return Regex.Replace(text, @"([a-zA-Z]{2,})-\s*[\r\n]+\s*([a-zA-Z]{2,})", m =>
            {
                string left = m.Groups[1].Value;
                string right = m.Groups[2].Value;
                string combined = left + right;

                if (combined.Length >= 4 && combined.All(char.IsLetter))
                    return combined;

                return m.Value;
            });
        }
    }
}
