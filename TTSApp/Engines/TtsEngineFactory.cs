using System;
using System.Collections.Generic;

namespace TTSApp
{
    /// <summary>
    /// Central place for creating and identifying TTS engines.
    /// Keeps engine-selection logic out of the UI layer.
    /// </summary>
    public static class TtsEngineFactory
    {
        public static readonly HashSet<string> SidecarModels = new(StringComparer.OrdinalIgnoreCase)
        {
            "xtts-v2", "chatterbox", "vibevoice"
        };

        public static readonly HashSet<string> KokoroModels = new(StringComparer.OrdinalIgnoreCase)
        {
            "kokoro-en-v0_19", "kokoro-multi-lang-v1_0", "kokoro-multi-lang-v1_1"
        };

        public static bool IsSidecarModel(string modelName) => SidecarModels.Contains(modelName);

        public static bool IsKnownModel(string modelName) =>
            SidecarModels.Contains(modelName) || KokoroModels.Contains(modelName);

        public static ITtsEngine CreateEngine(string modelName)
        {
            if (!IsKnownModel(modelName))
                throw new ArgumentException($"Unknown TTS model '{modelName}'.", nameof(modelName));

            return IsSidecarModel(modelName)
                ? new PythonSidecarEngine(modelName)
                : new TtsEngine(modelName);
        }

        /// <summary>
        /// Returns the provider string to pass to <see cref="ITtsEngine.Initialize"/> for a model.
        /// </summary>
        public static string ProviderFor(string modelName, int providerIndex) =>
            IsSidecarModel(modelName)
                ? "cuda"
                : providerIndex switch
                {
                    1 => "cuda",
                    2 => "dml",
                    _ => "cpu"
                };
    }
}
