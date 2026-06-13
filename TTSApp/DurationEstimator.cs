using System;
using System.Text.RegularExpressions;

namespace TTSApp
{
    public static class DurationEstimator
    {
        /// <summary>
        /// Rough heuristic for Kokoro TTS English audio duration.
        /// ~13 chars per second at 1.0x speed. Accounts for configured pauses.
        /// </summary>
        public static TimeSpan Estimate(string text, float speed)
        {
            if (string.IsNullOrWhiteSpace(text) || speed <= 0)
                return TimeSpan.Zero;

            const double charsPerSecond = 13.0;
            double seconds = text.Length / charsPerSecond / speed;

            // Add sentence pauses
            int sentenceCount = Regex.Matches(text, @"[.!?]+").Count;
            if (sentenceCount > 0)
                seconds += (sentenceCount - 1) * AppSettings.PauseAfterSentenceMs / 1000.0;

            // Add paragraph pauses
            int paragraphCount = text.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries).Length;
            if (paragraphCount > 0)
                seconds += paragraphCount * AppSettings.PauseAfterParagraphMs / 1000.0;

            return TimeSpan.FromSeconds(seconds);
        }

        public static string FormatFriendly(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
                return $"{duration.TotalHours:F1}h";
            if (duration.TotalMinutes >= 1)
                return $"{duration.TotalMinutes:F0}m";
            return $"{duration.TotalSeconds:F0}s";
        }
    }
}
