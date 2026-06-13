using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using SharpCompress.Readers;

namespace TTSApp
{
    public static class ModelDownloader
    {
        public static string ModelDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models");

        public static bool IsModelReady(string modelName)
        {
            var dir = Path.Combine(ModelDir, modelName);
            return File.Exists(Path.Combine(dir, "model.onnx"))
                && File.Exists(Path.Combine(dir, "voices.bin"))
                && File.Exists(Path.Combine(dir, "tokens.txt"));
        }

        public static bool IsModelReady()
        {
            return IsModelReady(AppSettings.SelectedModel);
        }

        public static string GetModelUrl(string modelName)
        {
            return modelName switch
            {
                "kokoro-multi-lang-v1_0" => "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-multi-lang-v1_0.tar.bz2",
                "kokoro-multi-lang-v1_1" => "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-multi-lang-v1_1.tar.bz2",
                _ => "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-en-v0_19.tar.bz2"
            };
        }

        public static async Task DownloadAsync(IProgress<double> progress)
        {
            await DownloadModelAsync(AppSettings.SelectedModel, progress);
        }

        public static async Task DownloadModelAsync(string modelName, IProgress<double> progress)
        {
            Directory.CreateDirectory(ModelDir);
            var url = GetModelUrl(modelName);
            var archivePath = Path.Combine(ModelDir, $"{modelName}_{Guid.NewGuid()}.tar.bz2");

            // Clean up any previous partial downloads
            foreach (var existing in Directory.GetFiles(ModelDir, $"{modelName}*.tar.bz2"))
            {
                try { File.Delete(existing); } catch { /* ignore */ }
            }

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(30);

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.Read);

            var buffer = new byte[81920];
            long readBytes = 0;
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                readBytes += bytesRead;
                if (totalBytes > 0)
                    progress.Report((double)readBytes / totalBytes);
            }
            await fileStream.FlushAsync();
            fileStream.Close();

            try
            {
                using (var stream = File.OpenRead(archivePath))
                using (var reader = ReaderFactory.Open(stream))
                {
                    while (reader.MoveToNextEntry())
                    {
                        if (!reader.Entry.IsDirectory)
                        {
                            reader.WriteEntryToDirectory(ModelDir, new SharpCompress.Common.ExtractionOptions
                            {
                                ExtractFullPath = true,
                                Overwrite = true
                            });
                        }
                    }
                }
            }
            finally
            {
                try { File.Delete(archivePath); } catch { /* ignore */ }
            }
        }
    }
}
