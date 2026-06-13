using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace TTSApp
{
    public static class AudioNormalizer
    {
        // ---------- #15: silence trimming ----------

        /// <summary>
        /// Trim leading/trailing near-silence from a WAV (keeps a small pad so it doesn't sound clipped).
        /// </summary>
        public static void TrimSilence(string wavPath, float thresholdDbfs = -45f, int padMs = 40)
        {
            if (!File.Exists(wavPath)) return;

            float[] samples;
            WaveFormat fmt;
            using (var reader = new WaveFileReader(wavPath))
            {
                fmt = reader.WaveFormat;
                samples = ReadAllMono(reader);
            }
            if (samples.Length == 0) return;

            float threshold = (float)Math.Pow(10.0, thresholdDbfs / 20.0);
            int first = 0, last = samples.Length - 1;
            while (first < samples.Length && Math.Abs(samples[first]) < threshold) first++;
            while (last > first && Math.Abs(samples[last]) < threshold) last--;
            if (first >= last) return; // all silence — leave as-is

            int pad = (int)(fmt.SampleRate * (padMs / 1000.0));
            first = Math.Max(0, first - pad);
            last = Math.Min(samples.Length - 1, last + pad);

            string tempPath = Path.Combine(Path.GetTempPath(), $"trim_{Guid.NewGuid()}.wav");
            using (var writer = new WaveFileWriter(tempPath, new WaveFormat(fmt.SampleRate, 16, 1)))
            {
                for (int i = first; i <= last; i++)
                {
                    short s = (short)Math.Clamp((int)(samples[i] * 32767f), short.MinValue, short.MaxValue);
                    writer.WriteByte((byte)(s & 0xFF));
                    writer.WriteByte((byte)((s >> 8) & 0xFF));
                }
            }
            File.Move(tempPath, wavPath, overwrite: true);
        }

        // ---------- #16: ITU-R BS.1770 integrated LUFS normalization ----------

        /// <summary>
        /// Normalize a WAV to a target integrated loudness (LUFS) per ITU-R BS.1770,
        /// with a true-peak-ish ceiling to avoid clipping. ACX target is about -18 to -23 LUFS.
        /// </summary>
        public static void NormalizeLufs(string wavPath, float targetLufs = -20f, float peakCeiling = 0.97f)
        {
            if (!File.Exists(wavPath)) return;

            float[] samples;
            int sampleRate;
            float maxPeak = 0f;
            using (var reader = new WaveFileReader(wavPath))
            {
                sampleRate = reader.WaveFormat.SampleRate;
                samples = ReadAllMono(reader);
            }
            if (samples.Length == 0) return;
            foreach (var s in samples) maxPeak = Math.Max(maxPeak, Math.Abs(s));
            if (maxPeak < 0.0001f) return; // silence

            double lufs = MeasureIntegratedLufs(samples, sampleRate);
            if (double.IsNegativeInfinity(lufs) || double.IsNaN(lufs)) return;

            float gain = (float)Math.Pow(10.0, (targetLufs - lufs) / 20.0);
            float maxGain = peakCeiling / maxPeak;
            if (gain > maxGain) gain = maxGain;
            if (Math.Abs(gain - 1f) < 0.01f) return;

            string tempPath = Path.Combine(Path.GetTempPath(), $"lufs_{Guid.NewGuid()}.wav");
            using (var writer = new WaveFileWriter(tempPath, new WaveFormat(sampleRate, 16, 1)))
            {
                foreach (var f in samples)
                {
                    short s = (short)Math.Clamp((int)(f * gain * 32767f), short.MinValue, short.MaxValue);
                    writer.WriteByte((byte)(s & 0xFF));
                    writer.WriteByte((byte)((s >> 8) & 0xFF));
                }
            }
            File.Move(tempPath, wavPath, overwrite: true);
        }

        // Integrated loudness with K-weighting + 400ms blocks (75% overlap) + absolute/relative gating.
        private static double MeasureIntegratedLufs(float[] x, int fs)
        {
            float[] k = KWeight(x, fs);

            int blockLen = (int)(0.400 * fs);
            int hop = (int)(0.100 * fs); // 75% overlap
            if (k.Length < blockLen) return double.NegativeInfinity;

            var blockMeanSq = new List<double>();
            for (int start = 0; start + blockLen <= k.Length; start += hop)
            {
                double sum = 0;
                for (int i = start; i < start + blockLen; i++) sum += (double)k[i] * k[i];
                blockMeanSq.Add(sum / blockLen);
            }
            if (blockMeanSq.Count == 0) return double.NegativeInfinity;

            // Absolute gate at -70 LUFS.
            const double absGate = -70.0;
            var gated = new List<double>();
            foreach (var z in blockMeanSq)
            {
                double l = -0.691 + 10.0 * Math.Log10(z + 1e-12);
                if (l >= absGate) gated.Add(z);
            }
            if (gated.Count == 0) return double.NegativeInfinity;

            // Relative gate: mean of gated, threshold = loudness(mean) - 10 LU.
            double meanZ = 0; foreach (var z in gated) meanZ += z; meanZ /= gated.Count;
            double relThresh = (-0.691 + 10.0 * Math.Log10(meanZ + 1e-12)) - 10.0;

            double finalSum = 0; int finalCount = 0;
            foreach (var z in gated)
            {
                double l = -0.691 + 10.0 * Math.Log10(z + 1e-12);
                if (l >= relThresh) { finalSum += z; finalCount++; }
            }
            if (finalCount == 0) return double.NegativeInfinity;

            double integratedZ = finalSum / finalCount;
            return -0.691 + 10.0 * Math.Log10(integratedZ + 1e-12);
        }

        // BS.1770 K-weighting: stage 1 high-shelf, stage 2 high-pass (coefficients computed for fs).
        private static float[] KWeight(float[] x, int fs)
        {
            // Stage 1 — high shelf (+4 dB)
            double f0 = 1681.974450955533, G = 3.999843853973347, Q = 0.7071752369554196;
            double K = Math.Tan(Math.PI * f0 / fs);
            double Vh = Math.Pow(10.0, G / 20.0);
            double Vb = Math.Pow(Vh, 0.4996667741545416);
            double a0 = 1.0 + K / Q + K * K;
            double b0 = (Vh + Vb * K / Q + K * K) / a0;
            double b1 = 2.0 * (K * K - Vh) / a0;
            double b2 = (Vh - Vb * K / Q + K * K) / a0;
            double a1 = 2.0 * (K * K - 1.0) / a0;
            double a2 = (1.0 - K / Q + K * K) / a0;
            float[] y = Biquad(x, b0, b1, b2, a1, a2);

            // Stage 2 — high-pass
            f0 = 38.13547087602444; Q = 0.5003270373238773;
            K = Math.Tan(Math.PI * f0 / fs);
            a0 = 1.0 + K / Q + K * K;
            b0 = 1.0; b1 = -2.0; b2 = 1.0;
            a1 = 2.0 * (K * K - 1.0) / a0;
            a2 = (1.0 - K / Q + K * K) / a0;
            b0 /= a0; b1 /= a0; b2 /= a0;
            return Biquad(y, b0, b1, b2, a1, a2);
        }

        private static float[] Biquad(float[] x, double b0, double b1, double b2, double a1, double a2)
        {
            var y = new float[x.Length];
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < x.Length; i++)
            {
                double xn = x[i];
                double yn = b0 * xn + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                y[i] = (float)yn;
                x2 = x1; x1 = xn; y2 = y1; y1 = yn;
            }
            return y;
        }

        private static float[] ReadAllMono(WaveFileReader reader)
        {
            var list = new List<float>((int)(reader.Length / 2));
            var buffer = new byte[reader.WaveFormat.BlockAlign * 4096];
            int bytesRead;
            var fmt = reader.WaveFormat;
            while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (fmt.BitsPerSample == 16)
                    for (int i = 0; i + 1 < bytesRead; i += 2)
                        list.Add((short)(buffer[i] | (buffer[i + 1] << 8)) / 32768f);
                else if (fmt.BitsPerSample == 32 && fmt.Encoding == WaveFormatEncoding.IeeeFloat)
                    for (int i = 0; i + 3 < bytesRead; i += 4)
                        list.Add(BitConverter.ToSingle(buffer, i));
                else if (fmt.BitsPerSample == 32)
                    for (int i = 0; i + 3 < bytesRead; i += 4)
                        list.Add(BitConverter.ToInt32(buffer, i) / 2147483648f);
            }
            return list.ToArray();
        }

        /// <summary>
        /// Simple peak normalization: scales entire file so peak hits target level.
        /// Target 0.95f = ~-0.45 dBFS headroom.
        /// </summary>
        public static void NormalizePeak(string wavPath, float targetPeak = 0.95f)
        {
            if (!File.Exists(wavPath)) return;

            string tempPath = Path.Combine(Path.GetTempPath(), $"norm_{Guid.NewGuid()}.wav");

            float maxPeak = 0f;

            // Pass 1: find peak
            using (var reader = new WaveFileReader(wavPath))
            {
                var buffer = new byte[reader.WaveFormat.BlockAlign * 4096];
                int bytesRead;
                while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    maxPeak = Math.Max(maxPeak, GetPeak(buffer, bytesRead, reader.WaveFormat));
                }
            }

            if (maxPeak < 0.0001f) return; // silence, nothing to do

            float gain = targetPeak / maxPeak;

            // Pass 2: apply gain
            using (var reader = new WaveFileReader(wavPath))
            {
                using var writer = new WaveFileWriter(tempPath, reader.WaveFormat);
                var buffer = new byte[reader.WaveFormat.BlockAlign * 4096];
                int bytesRead;
                while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ApplyGain(buffer, bytesRead, reader.WaveFormat, gain);
                    writer.Write(buffer, 0, bytesRead);
                }
            }

            File.Move(tempPath, wavPath, overwrite: true);
        }

        /// <summary>
        /// RMS-based loudness normalization toward a target level (default -20 dBFS RMS),
        /// with a peak ceiling so the gain never causes clipping. Closer to perceived loudness
        /// than peak normalization. (Not full ITU-R BS.1770 LUFS, but a solid practical approximation.)
        /// </summary>
        public static void NormalizeLoudness(string wavPath, float targetRmsDbfs = -20f, float peakCeiling = 0.97f)
        {
            if (!File.Exists(wavPath)) return;

            string tempPath = Path.Combine(Path.GetTempPath(), $"loud_{Guid.NewGuid()}.wav");

            double sumSquares = 0;
            long sampleCount = 0;
            float maxPeak = 0f;

            // Pass 1: RMS + peak
            using (var reader = new WaveFileReader(wavPath))
            {
                var buffer = new byte[reader.WaveFormat.BlockAlign * 4096];
                int bytesRead;
                while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    AccumulateStats(buffer, bytesRead, reader.WaveFormat, ref sumSquares, ref sampleCount, ref maxPeak);
                }
            }

            if (sampleCount == 0 || maxPeak < 0.0001f) return; // silence

            double rms = Math.Sqrt(sumSquares / sampleCount);
            if (rms < 1e-6) return;

            float targetRmsLinear = (float)Math.Pow(10.0, targetRmsDbfs / 20.0);
            float gain = (float)(targetRmsLinear / rms);

            // Don't let the gain push the peak past the ceiling (prevents clipping).
            float maxGain = peakCeiling / maxPeak;
            if (gain > maxGain) gain = maxGain;
            if (Math.Abs(gain - 1f) < 0.01f) return; // already close enough

            // Pass 2: apply gain
            using (var reader = new WaveFileReader(wavPath))
            {
                using var writer = new WaveFileWriter(tempPath, reader.WaveFormat);
                var buffer = new byte[reader.WaveFormat.BlockAlign * 4096];
                int bytesRead;
                while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ApplyGain(buffer, bytesRead, reader.WaveFormat, gain);
                    writer.Write(buffer, 0, bytesRead);
                }
            }

            File.Move(tempPath, wavPath, overwrite: true);
        }

        private static void AccumulateStats(byte[] buffer, int bytesRead, WaveFormat format,
            ref double sumSquares, ref long sampleCount, ref float maxPeak)
        {
            if (format.BitsPerSample == 16)
            {
                for (int i = 0; i + 1 < bytesRead; i += 2)
                {
                    float s = (short)(buffer[i] | (buffer[i + 1] << 8)) / 32768f;
                    sumSquares += (double)s * s;
                    sampleCount++;
                    maxPeak = Math.Max(maxPeak, Math.Abs(s));
                }
            }
            else if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                for (int i = 0; i + 3 < bytesRead; i += 4)
                {
                    float s = BitConverter.ToSingle(buffer, i);
                    sumSquares += (double)s * s;
                    sampleCount++;
                    maxPeak = Math.Max(maxPeak, Math.Abs(s));
                }
            }
            else if (format.BitsPerSample == 32)
            {
                for (int i = 0; i + 3 < bytesRead; i += 4)
                {
                    float s = BitConverter.ToInt32(buffer, i) / 2147483648f;
                    sumSquares += (double)s * s;
                    sampleCount++;
                    maxPeak = Math.Max(maxPeak, Math.Abs(s));
                }
            }
        }

        private static float GetPeak(byte[] buffer, int bytesRead, WaveFormat format)
        {
            float peak = 0f;
            if (format.BitsPerSample == 16)
            {
                for (int i = 0; i < bytesRead; i += 2)
                {
                    short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                    peak = Math.Max(peak, Math.Abs(sample / 32768f));
                }
            }
            else if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                for (int i = 0; i < bytesRead; i += 4)
                {
                    float sample = BitConverter.ToSingle(buffer, i);
                    peak = Math.Max(peak, Math.Abs(sample));
                }
            }
            else if (format.BitsPerSample == 32)
            {
                for (int i = 0; i < bytesRead; i += 4)
                {
                    int sample = BitConverter.ToInt32(buffer, i);
                    peak = Math.Max(peak, Math.Abs(sample / 2147483648f));
                }
            }
            return peak;
        }

        private static void ApplyGain(byte[] buffer, int bytesRead, WaveFormat format, float gain)
        {
            if (format.BitsPerSample == 16)
            {
                for (int i = 0; i < bytesRead; i += 2)
                {
                    short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                    int scaled = (int)(sample * gain);
                    if (scaled > short.MaxValue) scaled = short.MaxValue;
                    if (scaled < short.MinValue) scaled = short.MinValue;
                    buffer[i] = (byte)(scaled & 0xFF);
                    buffer[i + 1] = (byte)((scaled >> 8) & 0xFF);
                }
            }
            else if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                for (int i = 0; i < bytesRead; i += 4)
                {
                    float sample = BitConverter.ToSingle(buffer, i);
                    float scaled = sample * gain;
                    if (scaled > 1.0f) scaled = 1.0f;
                    if (scaled < -1.0f) scaled = -1.0f;
                    var bytes = BitConverter.GetBytes(scaled);
                    Buffer.BlockCopy(bytes, 0, buffer, i, 4);
                }
            }
            else if (format.BitsPerSample == 32)
            {
                for (int i = 0; i < bytesRead; i += 4)
                {
                    int sample = BitConverter.ToInt32(buffer, i);
                    long scaled = (long)(sample * gain);
                    if (scaled > int.MaxValue) scaled = int.MaxValue;
                    if (scaled < int.MinValue) scaled = int.MinValue;
                    var bytes = BitConverter.GetBytes((int)scaled);
                    Buffer.BlockCopy(bytes, 0, buffer, i, 4);
                }
            }
        }
    }
}
