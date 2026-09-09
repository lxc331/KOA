using System;
using UnityEngine;

namespace RehabPhotoGame
{
    /// <summary>运行时生成很短的相机快门声，避免第二段额外依赖音频文件。</summary>
    public static class ShutterSound
    {
        public const int SampleRate = 44100;
        public const float DurationSeconds = 0.22f;

        public static AudioClip CreateClip()
        {
            float[] samples = BuildSamples(SampleRate);
            AudioClip clip = AudioClip.Create(
                "S1 Camera Shutter", samples.Length, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        public static float[] BuildSamples(int sampleRate)
        {
            if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            int length = Math.Max(1, (int)Math.Round(sampleRate * DurationSeconds));
            var output = new float[length];
            var random = new System.Random(20260908);

            for (int i = 0; i < length; i++)
            {
                float t = i / (float)sampleRate;
                float first = ClickEnvelope(t, 0.000f, 0.042f);
                float second = ClickEnvelope(t, 0.074f, 0.065f);
                float noise = (float)(random.NextDouble() * 2.0 - 1.0);
                float metallic = (float)Math.Sin(2.0 * Math.PI * 1180.0 * t);
                float lowClick = (float)Math.Sin(2.0 * Math.PI * 170.0 * t);
                float value = noise * (first * 0.52f + second * 0.34f) +
                              metallic * first * 0.22f +
                              lowClick * second * 0.28f;
                output[i] = Math.Max(-0.95f, Math.Min(0.95f, value));
            }
            return output;
        }

        private static float ClickEnvelope(float time, float start, float duration)
        {
            float local = time - start;
            if (local < 0f || local >= duration) return 0f;
            float attack = Math.Min(1f, local / 0.003f);
            float decay = 1f - local / duration;
            return attack * decay * decay;
        }
    }
}
