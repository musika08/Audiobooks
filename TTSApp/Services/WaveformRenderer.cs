using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace TTSApp.Services
{
    /// <summary>
    /// Renders a waveform as point sequences that can be turned into WPF polylines on the UI thread.
    /// Keeps heavy audio sample processing off the dispatcher.
    /// </summary>
    public static class WaveformRenderer
    {
        public static IReadOnlyList<IReadOnlyList<System.Windows.Point>> Render(string wavPath, int width, int height)
        {
            using var reader = new WaveFileReader(wavPath);
            int channels = reader.WaveFormat.Channels;
            int bytesPerSample = reader.WaveFormat.BitsPerSample / 8;
            long totalSamples = reader.Length / (channels * bytesPerSample);

            int samplesPerPixel = Math.Max(1, (int)(totalSamples / width));
            var buffer = new byte[reader.WaveFormat.BlockAlign * samplesPerPixel];

            var top = new List<System.Windows.Point>(width);
            double midY = height / 2.0;

            for (int x = 0; x < width; x++)
            {
                int bytesRead = reader.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                float peak = ComputePeak(buffer, bytesRead, bytesPerSample, channels, reader.WaveFormat.Encoding);
                double y = midY - (peak * midY);
                top.Add(new System.Windows.Point(x, y));
            }

            var bottom = new List<System.Windows.Point>(top.Count);
            foreach (var pt in top)
                bottom.Add(new System.Windows.Point(pt.X, midY + (midY - pt.Y)));

            return new[] { top, bottom };
        }

        private static float ComputePeak(byte[] buffer, int bytesRead, int bytesPerSample, int channels, WaveFormatEncoding encoding)
        {
            float peak = 0f;
            if (bytesPerSample == 2)
            {
                for (int i = 0; i < bytesRead; i += 2 * channels)
                {
                    short s = (short)(buffer[i] | (buffer[i + 1] << 8));
                    peak = Math.Max(peak, Math.Abs(s / 32768f));
                }
            }
            else if (bytesPerSample == 4 && encoding == WaveFormatEncoding.IeeeFloat)
            {
                for (int i = 0; i < bytesRead; i += 4 * channels)
                {
                    float s = BitConverter.ToSingle(buffer, i);
                    peak = Math.Max(peak, Math.Abs(s));
                }
            }
            return peak;
        }
    }
}
