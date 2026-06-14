using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TTSApp.AI
{
    /// <summary>
    /// Local Ollama client (http://localhost:11434). Free and fully offline — text never leaves the
    /// machine. Uses the native /api/chat endpoint with streaming disabled.
    /// </summary>
    public sealed class OllamaProvider : IAiProvider
    {
        // Generous timeout: local models on CPU can be slow for a full chapter.
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

        private readonly string _model;
        private readonly string _baseUrl;

        public OllamaProvider(string model, string baseUrl = "http://localhost:11434")
        {
            _model = string.IsNullOrWhiteSpace(model) ? "qwen2.5:7b" : model;
            _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:11434" : baseUrl.TrimEnd('/');
        }

        public async Task<string> CompleteAsync(string system, string user, CancellationToken token)
        {
            var payload = new
            {
                model = _model,
                stream = false,
                options = new { temperature = 0.2 },
                messages = new[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user },
                },
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };

            HttpResponseMessage resp;
            try
            {
                resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(
                    $"Could not reach Ollama at {_baseUrl}. Is it installed and running? ({ex.Message})", ex);
            }

            using (resp)
            {
                string body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Ollama request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}\n{body}");

                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
            }
        }
    }
}
