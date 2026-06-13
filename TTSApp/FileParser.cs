using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using VersOne.Epub;

namespace TTSApp
{
    public static class FileParser
    {
        public static List<ChapterItem> Parse(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".txt" => ParseTxt(filePath),
                ".pdf" => ParsePdf(filePath),
                ".epub" => ParseEpub(filePath),
                _ => throw new NotSupportedException($"File type '{ext}' is not supported. Use .txt, .pdf, or .epub")
            };
        }

        private static List<ChapterItem> ParseTxt(string path)
        {
            var text = File.ReadAllText(path);
            return SplitByChapters(text, Path.GetFileNameWithoutExtension(path));
        }

        private static List<ChapterItem> SplitByChapters(string text, string defaultTitle)
        {
            var chapters = new List<ChapterItem>();
            var regex = new Regex(@"(?:^|\n\s*)(?:Chapter\s+\d+[\.\:\s]*.*|CHAPTER\s+[IVX\d]+.*|^\d+\.\s+.+)(?:\r?\n)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            var matches = regex.Matches(text);

            if (matches.Count == 0)
            {
                chapters.Add(new ChapterItem
                {
                    Title = defaultTitle,
                    Content = text.Trim(),
                    Index = 0,
                    IsSelected = true
                });
                return chapters;
            }

            int lastIndex = 0;
            string lastTitle = "Introduction";

            for (int i = 0; i < matches.Count; i++)
            {
                int start = matches[i].Index;
                if (start > lastIndex)
                {
                    string content = text.Substring(lastIndex, start - lastIndex).Trim();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        chapters.Add(new ChapterItem
                        {
                            Title = lastTitle,
                            Content = content,
                            Index = chapters.Count,
                            IsSelected = true
                        });
                    }
                }
                lastIndex = start + matches[i].Length;
                lastTitle = matches[i].Value.Trim();
            }

            if (lastIndex < text.Length)
            {
                string content = text.Substring(lastIndex).Trim();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    chapters.Add(new ChapterItem
                    {
                        Title = lastTitle,
                        Content = content,
                        Index = chapters.Count,
                        IsSelected = true
                    });
                }
            }

            return chapters;
        }

        private static List<ChapterItem> ParsePdf(string path)
        {
            var pages = new List<string>();
            using (var document = PdfDocument.Open(path))
            {
                foreach (var page in document.GetPages())
                {
                    // Normalize page text: collapse multiple spaces, preserve single newlines
                    var text = page.Text;
                    text = Regex.Replace(text, @"[ \t]+", " ");
                    text = Regex.Replace(text, @"\n\s*\n", "\n\n");
                    pages.Add(text.Trim());
                }
            }
            pages = TextCleaner.CleanPdfPages(pages);
            var fullText = string.Join("\n\n", pages);
            return SplitByChapters(fullText, Path.GetFileNameWithoutExtension(path));
        }

        private static List<ChapterItem> ParseEpub(string path)
        {
            try
            {
                return ParseEpubWithLibrary(path);
            }
            catch
            {
                return ParseEpubManualFallback(path);
            }
        }

        private static List<ChapterItem> ParseEpubWithLibrary(string path)
        {
            var chapters = new List<ChapterItem>();
            var book = EpubReader.ReadBook(path);
            int idx = 0;

            foreach (var contentFile in book.ReadingOrder)
            {
                var html = contentFile.Content;
                var text = ExtractTextFromHtml(html);

                if (string.IsNullOrWhiteSpace(text)) continue;

                var title = contentFile.FilePath;
                // Try navigation/TOC first
                if (book.Navigation != null)
                {
                    var navItem = book.Navigation.FirstOrDefault(n =>
                        n.Link != null && string.Equals(n.Link.ContentFilePath, contentFile.FilePath, StringComparison.OrdinalIgnoreCase));
                    if (navItem != null && !string.IsNullOrWhiteSpace(navItem.Title))
                        title = navItem.Title;
                }
                // Fallback: use filename without extension
                if (string.IsNullOrWhiteSpace(title) || title.Contains('/'))
                {
                    title = Path.GetFileNameWithoutExtension(contentFile.FilePath);
                }
                // If title is a generic filename (e.g. "Section0001"), derive from content
                if (IsGenericTitle(title))
                {
                    title = DeriveTitleFromContent(text, idx);
                }

                chapters.Add(new ChapterItem
                {
                    Title = title,
                    Content = StripLeadingTitle(text, title),
                    Index = idx,
                    IsSelected = true
                });
                idx++;
            }

            if (chapters.Count == 0)
            {
                var allText = string.Join("\n\n", book.ReadingOrder.Select(c => ExtractTextFromHtml(c.Content)));
                chapters.Add(new ChapterItem
                {
                    Title = book.Title ?? "Ebook",
                    Content = allText,
                    Index = 0,
                    IsSelected = true
                });
            }

            return chapters;
        }

        private static List<ChapterItem> ParseEpubManualFallback(string path)
        {
            var chapters = new List<ChapterItem>();
            using var archive = ZipFile.OpenRead(path);
            int idx = 0;

            var htmlEntries = archive.Entries
                .Where(e => e.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase)
                         || e.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName)
                .ToList();

            foreach (var entry in htmlEntries)
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                var html = reader.ReadToEnd();
                var text = ExtractTextFromHtml(html);

                if (string.IsNullOrWhiteSpace(text)) continue;

                var title = Path.GetFileNameWithoutExtension(entry.FullName);
                if (IsGenericTitle(title))
                {
                    title = DeriveTitleFromContent(text, idx);
                }
                chapters.Add(new ChapterItem
                {
                    Title = title,
                    Content = StripLeadingTitle(text, title),
                    Index = idx,
                    IsSelected = true
                });
                idx++;
            }

            if (chapters.Count == 0)
            {
                chapters.Add(new ChapterItem
                {
                    Title = "Ebook",
                    Content = "Could not extract chapters from EPUB.",
                    Index = 0,
                    IsSelected = true
                });
            }

            return chapters;
        }

        // Matches generic auto-generated names like "Section0001", "part_3", "ch12", "page 5"
        private static bool IsGenericTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return true;
            return Regex.IsMatch(title.Trim(),
                @"^(section|part|chapter|ch|page|pg|split|text|item|content|index|cover|toc)?[\s_\-]*\d+$",
                RegexOptions.IgnoreCase);
        }

        // Use the first meaningful line of content as the chapter title
        private static string DeriveTitleFromContent(string text, int idx)
        {
            var firstLine = text
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0);

            if (string.IsNullOrWhiteSpace(firstLine))
                return $"Chapter {idx + 1}";

            if (firstLine.Length > 60)
                firstLine = firstLine.Substring(0, 60).TrimEnd() + "…";

            return firstLine;
        }

        // Remove leading content lines that duplicate the chapter title (EPUBs often repeat the heading in the body)
        private static string StripLeadingTitle(string text, string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return text;
            var titleNorm = title.TrimEnd('…').Trim();

            var lines = text.Split('\n').ToList();
            int removed = 0;
            while (lines.Count > 0 && removed < 3)
            {
                var line = lines[0].Trim();
                if (line.Length == 0)
                {
                    lines.RemoveAt(0);
                    continue;
                }
                if (string.Equals(line, titleNorm, StringComparison.OrdinalIgnoreCase))
                {
                    lines.RemoveAt(0);
                    removed++;
                    continue;
                }
                break;
            }
            return removed > 0 ? string.Join("\n", lines).TrimStart('\n', ' ') : text;
        }

        private static string ExtractTextFromHtml(string html)
        {
            // Preserve block-level structure by replacing tags with newlines before stripping
            html = html.Replace("</p>", "\n\n")
                       .Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n")
                       .Replace("</div>", "\n")
                       .Replace("</h1>", "\n\n").Replace("</h2>", "\n\n").Replace("</h3>", "\n\n")
                       .Replace("</h4>", "\n").Replace("</h5>", "\n").Replace("</h6>", "\n")
                       .Replace("</li>", "\n")
                       .Replace("</tr>", "\n")
                       .Replace("</td>", " \t");
            html = Regex.Replace(html, "<[^>]+>", "");
            html = html.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"");
            // Normalize whitespace but keep paragraph breaks
            html = Regex.Replace(html, @"[ \t]+", " ");
            html = Regex.Replace(html, @"\n\s*\n", "\n\n");
            html = html.Trim();
            return html;
        }
    }
}
