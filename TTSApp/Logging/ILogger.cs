using System;

namespace TTSApp.Logging
{
    /// <summary>
    /// Minimal logging surface used throughout the app.
    /// </summary>
    public interface ILogger
    {
        void LogDebug(string message);
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message);
        void LogError(Exception exception, string message);
    }
}
