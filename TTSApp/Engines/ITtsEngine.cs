using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

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

        /// <summary>
        /// Async, cancellable version of <see cref="Generate"/>.
        /// The default implementation runs the synchronous method on the thread pool.
        /// </summary>
        Task GenerateAsync(string text, int speakerId, float speed, string outputPath, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);

            return Task.Run(() => Generate(text, speakerId, speed, outputPath), cancellationToken);
        }
    }
}
