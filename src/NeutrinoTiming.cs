namespace TuneLab.NeutrinoV3;

internal readonly record struct PhoneChunk(int PhoneStart, int PhoneCount, bool IsActive);

internal readonly record struct FrameChunk(
    int PhoneStart,
    int PhoneCount,
    int FrameStart,
    int FrameCount,
    bool IsActive);

internal static class NeutrinoTiming
{
    public static float[] RequireLength(float[] values, int expectedLength, string outputName)
    {
        if (values.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"{outputName} length mismatch: actual {values.Length}, expected {expectedLength}.");
        }
        return values;
    }

    public static PhoneChunk[] BuildPhoneChunks(long[] phonemeIds)
    {
        var chunks = new List<PhoneChunk>();
        if (phonemeIds.Length == 0)
            return [];

        int chunkStart = 0;
        bool chunkIsActive = true;
        bool inPause = false;
        bool afterBreath = false;
        for (int phone = 0; phone < phonemeIds.Length; phone++)
        {
            if (phonemeIds[phone] == NeutrinoPhonemes.Pau)
            {
                if (!inPause)
                {
                    if (phone > chunkStart)
                        chunks.Add(new PhoneChunk(chunkStart, phone - chunkStart, chunkIsActive));
                    chunkStart = phone;
                    chunkIsActive = false;
                    inPause = true;
                    afterBreath = false;
                }
                continue;
            }

            if (phonemeIds[phone] == NeutrinoPhonemes.Br)
            {
                inPause = false;
                afterBreath = true;
                continue;
            }

            if (inPause || afterBreath)
            {
                chunks.Add(new PhoneChunk(chunkStart, phone - chunkStart, chunkIsActive));
                chunkStart = phone;
                chunkIsActive = true;
                inPause = false;
                afterBreath = false;
            }
        }
        chunks.Add(new PhoneChunk(chunkStart, phonemeIds.Length - chunkStart, chunkIsActive));
        return chunks.ToArray();
    }

    public static double[] BuildTimingBoundaries(
        float[] scoreDurations,
        long[] phonePositions,
        PhoneChunk[] chunks,
        double frameSeconds,
        Func<PhoneChunk, float[]> predictBoundaryShifts,
        double leadingContextSeconds)
    {
        if (scoreDurations.Length != phonePositions.Length)
            throw new ArgumentException("Score duration and phone position lengths must match.");

        double[] baseBoundaries = BuildBaseBoundaryTimes(scoreDurations, phonePositions);
        var shifts = new float[baseBoundaries.Length];
        foreach (PhoneChunk chunk in chunks)
        {
            if (!chunk.IsActive || chunk.PhoneCount <= 0)
                continue;
            float[] chunkShifts = predictBoundaryShifts(chunk);
            int expected = checked(chunk.PhoneCount + 1);
            if (chunkShifts.Length != expected)
            {
                throw new InvalidDataException(
                    $"NEUTRINO v3 t.bin timing output length mismatch: " +
                    $"actual {chunkShifts.Length}, expected {expected}.");
            }

            // The official loader uses one shift per phone and ignores the extra
            // final model value before applying all shifts on the global timeline.
            Array.Copy(chunkShifts, 0, shifts, chunk.PhoneStart, chunk.PhoneCount);
        }
        return ApplyBoundaryShifts(baseBoundaries, shifts, frameSeconds, leadingContextSeconds);
    }

    public static FrameChunk[] BuildFrameChunks(
        PhoneChunk[] phoneChunks,
        double[] normalizedBoundaries,
        int totalFrames,
        double frameSeconds)
    {
        var result = new FrameChunk[phoneChunks.Length];
        for (int i = 0; i < result.Length; i++)
        {
            PhoneChunk chunk = phoneChunks[i];
            int frameStart = Math.Clamp(
                (int)Math.Round(normalizedBoundaries[chunk.PhoneStart] / frameSeconds),
                0,
                totalFrames);
            int frameEnd = Math.Clamp(
                (int)Math.Round(normalizedBoundaries[chunk.PhoneStart + chunk.PhoneCount] / frameSeconds),
                frameStart,
                totalFrames);
            result[i] = new FrameChunk(
                chunk.PhoneStart,
                chunk.PhoneCount,
                frameStart,
                frameEnd - frameStart,
                chunk.IsActive);
        }
        return result;
    }

    public static long[] BuildFramePhonemeMap(float[] durations, int totalFrames, double frameSeconds)
    {
        var result = new long[totalFrames];
        double time = 0;
        for (int phone = 0; phone < durations.Length; phone++)
        {
            int startFrame = Math.Clamp((int)Math.Round(time / frameSeconds), 0, totalFrames);
            time += durations[phone];
            int endFrame = Math.Clamp((int)Math.Round(time / frameSeconds), startFrame, totalFrames);
            for (int frame = startFrame; frame < endFrame; frame++)
                result[frame] = phone + 1;
        }

        long finalPhone = Math.Max(1, durations.Length);
        for (int frame = 0; frame < result.Length; frame++)
        {
            if (result[frame] == 0)
                result[frame] = finalPhone;
        }
        return result;
    }

    public static T[] Slice<T>(T[] values, int start, int length)
    {
        var result = new T[length];
        Array.Copy(values, start, result, 0, length);
        return result;
    }

    static double[] BuildBaseBoundaryTimes(float[] scoreDurations, long[] phonePositions)
    {
        var boundaries = new double[scoreDurations.Length + 1];
        double time = 0;
        for (int i = 0; i < scoreDurations.Length; i++)
        {
            boundaries[i] = time;
            long nextPosition = i + 1 < phonePositions.Length ? phonePositions[i + 1] : -1;
            if (i == scoreDurations.Length - 1 || nextPosition <= phonePositions[i])
                time += scoreDurations[i];
        }
        boundaries[^1] = time;
        return boundaries;
    }

    static double[] ApplyBoundaryShifts(
        double[] baseBoundaries,
        float[] shifts,
        double frameSeconds,
        double leadingContextSeconds)
    {
        var boundaries = (double[])baseBoundaries.Clone();
        if (boundaries.Length > 1)
        {
            double context = Math.Max(0, leadingContextSeconds);
            double minimum = Math.Min(0, -context + frameSeconds);
            boundaries[0] = MillisecondRound(Math.Max(baseBoundaries[0] + shifts[0], minimum));
        }
        for (int i = 1; i < boundaries.Length - 1; i++)
        {
            double shifted = baseBoundaries[i] + shifts[i];
            boundaries[i] = MillisecondRound(Math.Max(shifted, boundaries[i - 1] + frameSeconds));
        }
        for (int i = 1; i < boundaries.Length; i++)
        {
            if (boundaries[i] <= boundaries[i - 1])
                boundaries[i] = MillisecondRound(boundaries[i - 1] + frameSeconds);
        }
        return boundaries;
    }

    static double MillisecondRound(double value) => Math.Round(value * 1000.0) / 1000.0;
}
