using System;
using System.IO;

namespace TTSApp.Logging
{
    /// <summary>
    /// A simple file logger that appends timestamped lines to a log file in the user's LocalAppData.
    /// Logging failures are swallowed so logging can never crash the app.
    /// </summary>
    public sealed class FileLogger : ILogger
    {
        private readonly string _logFilePath;
        private readonly object _lock = new();

        public FileLogger(string? logFilePath = null)
        {
            _logFilePath = logFilePath ?? DefaultLogPath();
            try
            {
                var dir = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch
            {
                // Best-effort; logging must never throw.
            }
        }

        public static string DefaultLogPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, "TTSApp", "logs", "app.log");
        }

        public void LogDebug(string message) => Write("DBG", message);
        public void LogInfo(string message) => Write("INF", message);
        public void LogWarning(string message) => Write("WRN", message);
        public void LogError(string message) => Write("ERR", message);

        public void LogError(Exception exception, string message)
        {
            Write("ERR", $"{message}{Environment.NewLine}{exception}");
        }

        private void Write(string level, string message)
        {
            try
            {
                var threadId = Environment.CurrentManagedThreadId;
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] #{threadId} {message}";
                lock (_lock)
                {
                    File.AppendAllText(_logFilePath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never throw.
            }
        }
    }
}
