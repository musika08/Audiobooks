using System;

namespace TTSApp.Logging
{
    /// <summary>
    /// Static logging facade. Defaults to a file logger; replace with <see cref="SetLogger(ILogger)"/> for tests.
    /// </summary>
    public static class Log
    {
        private static ILogger _logger = new FileLogger();
        private static readonly object _lock = new();

        public static void SetLogger(ILogger logger)
        {
            lock (_lock) _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public static ILogger Current
        {
            get { lock (_lock) return _logger; }
        }

        public static void Debug(string message) => Current.LogDebug(message);
        public static void Info(string message) => Current.LogInfo(message);
        public static void Warn(string message) => Current.LogWarning(message);
        public static void Error(string message) => Current.LogError(message);
        public static void Error(Exception exception, string message) => Current.LogError(exception, message);
    }
}
