using System.Threading;
using System.Threading.Tasks;

namespace TTSApp.AI
{
    /// <summary>
    /// A text-completion backend for the optional AI text-assist feature. Implementations send a
    /// system instruction + user text to a hosted (or local) LLM and return the raw text response.
    /// </summary>
    public interface IAiProvider
    {
        /// <summary>
        /// Run a single completion. <paramref name="system"/> is the instruction, <paramref name="user"/>
        /// is the content to act on. Returns the model's text reply. Throws on transport/HTTP errors.
        /// </summary>
        Task<string> CompleteAsync(string system, string user, CancellationToken token);
    }
}
