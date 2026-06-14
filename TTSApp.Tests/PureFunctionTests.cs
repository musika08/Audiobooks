using System;
using System.IO;
using System.Linq;
using NAudio.Wave;
using TTSApp;
using TTSApp.Services;
using Xunit;

namespace TTSApp.Tests
{
    public class PureFunctionTests : IDisposable
    {
        private readonly string _tempDir;

        public PureFunctionTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"TTSAppTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
            GC.SuppressFinalize(this);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ResolveCloneReferencePath_RejectsEmpty(string? input)
        {
            Assert.Null(AppSettings.ResolveCloneReferencePath(input));
        }

        [Theory]
        [InlineData("voice.txt")]
        [InlineData("voice.exe")]
        [InlineData("voice")]
        public void ResolveCloneReferencePath_RejectsUnsupportedExtensions(string input)
        {
            Assert.Null(AppSettings.ResolveCloneReferencePath(input));
        }

        [Fact]
        public void ResolveCloneReferencePath_CopiesExternalFileIntoVoicesDirectory()
        {
            string source = Path.Combine(_tempDir, "source.wav");
            File.WriteAllText(source, "fake wav content");

            string? resolved = AppSettings.ResolveCloneReferencePath(source);

            Assert.NotNull(resolved);
            Assert.True(File.Exists(resolved), "Resolved file should exist");
            Assert.Equal(Path.GetFullPath(resolved), Path.GetFullPath(resolved)); // sanity
            Assert.StartsWith(Path.GetFullPath(AppSettings.VoicesDir), resolved);
        }

        [Fact]
        public void TtsEngineFactory_KnownModels_AreClassifiedCorrectly()
        {
            Assert.True(TtsEngineFactory.IsKnownModel("xtts-v2"));
            Assert.True(TtsEngineFactory.IsKnownModel("chatterbox"));
            Assert.True(TtsEngineFactory.IsKnownModel("kokoro-en-v0_19"));
            Assert.False(TtsEngineFactory.IsKnownModel("unknown-model"));

            Assert.True(TtsEngineFactory.IsSidecarModel("xtts-v2"));
            Assert.False(TtsEngineFactory.IsSidecarModel("kokoro-multi-lang-v1_1"));
        }

        [Fact]
        public void TtsEngineFactory_ThrowsForUnknownModel()
        {
            Assert.Throws<ArgumentException>(() => TtsEngineFactory.CreateEngine("not-a-model"));
        }

        [Fact]
        public void WaveformRenderer_Render_ReturnsTopAndBottomPolylines()
        {
            string wavPath = Path.Combine(_tempDir, "test.wav");
            WriteMinimalWav(wavPath, sampleRate: 16000, durationSeconds: 1);

            var polylines = WaveformRenderer.Render(wavPath, width: 200, height: 60);

            Assert.Equal(2, polylines.Count);
            Assert.True(polylines[0].Count > 0);
            Assert.True(polylines[1].Count > 0);
            Assert.All(polylines[0], p => Assert.True(p.Y >= 0 && p.Y <= 60));
        }

        private static void WriteMinimalWav(string path, int sampleRate, int durationSeconds)
        {
            var format = new WaveFormat(sampleRate, 16, 1);
            using var writer = new WaveFileWriter(path, format);
            var samples = Enumerable.Range(0, sampleRate * durationSeconds)
                .Select(i => (short)(Math.Sin(i * Math.PI * 2 / sampleRate) * short.MaxValue));
            foreach (var s in samples)
                writer.WriteSample(s / 32768f);
        }
    }
}
