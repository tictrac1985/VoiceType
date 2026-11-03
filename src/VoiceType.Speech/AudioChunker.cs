namespace VoiceType.Speech;

/// <summary>
/// تقسيم الصوت الطويل لأجزاء قصيرة عند أهدى نقطة (وقفة بين الكلام)، للنماذج اللي متدربة على مقاطع قصيرة
/// زي Cohere Transcribe. التقسيم عند الوقفة بيمنع إن كلمة تتقطع نصين.
/// </summary>
public static class AudioChunker
{
    public readonly record struct Chunk(int Start, int Length);

    /// <param name="maxSeconds">أطول جزء.</param>
    /// <param name="minSeconds">أقصر جزء قبل ما ندوّر على وقفة (عشان منقسمش على أول نَفَس).</param>
    public static IReadOnlyList<Chunk> Split(ReadOnlySpan<float> samples, int sampleRate = 16000, double maxSeconds = 28, double minSeconds = 15)
    {
        var chunks = new List<Chunk>();
        var max = (int)(maxSeconds * sampleRate);
        var min = (int)(minSeconds * sampleRate);
        var window = Math.Max(1, sampleRate / 5); // 0.2 ثانية
        var start = 0;
        while (samples.Length - start > max)
        {
            var best = start + max;
            var bestEnergy = double.MaxValue;
            for (var position = start + min; position + window <= start + max; position += window / 2)
            {
                double energy = 0;
                foreach (var sample in samples.Slice(position, window))
                {
                    energy += sample * sample;
                }

                if (energy < bestEnergy)
                {
                    bestEnergy = energy;
                    best = position + (window / 2);
                }
            }

            chunks.Add(new Chunk(start, best - start));
            start = best;
        }

        if (samples.Length - start > 0)
        {
            chunks.Add(new Chunk(start, samples.Length - start));
        }

        return chunks;
    }
}
