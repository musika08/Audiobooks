using System;
using System.Collections.Generic;

namespace TTSApp
{
    // Common surface the UI uses regardless of backend (sherpa Kokoro in-process, or Python sidecar).
    public interface ITtsEngine : IDisposable
    {
        bool IsInitialized { get; }
        string CurrentProvider { get; }

        void Initialize(string provider);
        List<string> GetSpeakerNames();
        void Generate(string text, int speakerId, float speed, string outputPath);
    }
}
