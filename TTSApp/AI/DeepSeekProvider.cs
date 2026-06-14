using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TTSApp.AI
{
    /// <summary>
    /// DeepSeek chat-completions client (OpenAI-compatible wire format). Cheapest provider for the
    /// AI text-assist feature. Endpoint: https://api.deepseek.com/chat/completions.
    /// </summary>
    public sealed class DeepSeekProvider : IAiProvider
    {
        private const string Endpoint = "https://api.deepseek.com/chat/completions";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

        private readonly string _apiKey;
        private readonly string _model;

        public DeepSeekProvider(string apiKey, string model)
        {
            _apiKey = apiKey;
            _model = string.IsNullOrWhiteSpace(model) ? "deepseek-v4-flash" : model;
        }

        public async Task<string> CompleteAsync(string system, string user, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
                throw new InvalidOperationException("DeepSeek API key is not set (Settings → AI Assist).");

            var payload = new
            {
                model = _model,
                stream = false,
                temperature = 0.2,   // near-deterministic: this is cleanup, not creative writing
                messages = new[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user },
                },
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"DeepSeek request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}\n{body}");

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
        }
    }
}
