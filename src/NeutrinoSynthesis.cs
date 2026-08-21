using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TuneLab.Foundation;
using TuneLab.SDK;

namespace TuneLab.NeutrinoV3;

internal sealed record NeutrinoRenderedBlock(
    long SampleOffset,
    float[] Audio,
    IReadOnlyMap<string, SynthesizedSyllable> Phonemes,
    IReadOnlyList<IReadOnlyList<Point>> PitchSegments);

internal static class NeutrinoSynthesis
{
    public const int SampleRate = 48000;
    public const int HopSize = 480;
    const int MelBins = 100;
    const double FrameSeconds = (double)HopSize / SampleRate;
    const int EdgeSilenceSamples = 240;
    const int FadeInSamples = 240;
    const int FadeOutSamples = 240;
    const float F0Min = 40f;
    const float F0Max = 2000f;
    const float MelspecMin = -7f;
    const float MelspecMax = 1f;
    const float WaveScale = 0.9885531068f;
    const float WaveClamp = 0.9988493919f;
    const float StyleShiftMinCents = -1200f;
    const float StyleShiftMaxCents = 1200f;

    public static NeutrinoRenderedBlock? Render(
        NeutrinoVoicebank voicebank,
        VoiceSynthesisSnapshot snapshot,
        IReadOnlyList<bool> continuation,
        double availableLeadingSeconds,
        IProgress<double>? progress,
        CancellationToken cancellation)
    {
        if (snapshot.Notes.Count == 0)
            return new NeutrinoRenderedBlock(0, [], new Map<string, SynthesizedSyllable>(), []);
        if (continuation.Count != snapshot.Notes.Count)
            throw new ArgumentException("Continuation flags must align with snapshot notes.");

        List<NotePlan> plans = BuildPlans(snapshot, continuation);
        if (plans.Count == 0)
        {
            double start = snapshot.Notes[0].StartTime;
            return new NeutrinoRenderedBlock(
                (long)Math.Round(start * SampleRate),
                [],
                new Map<string, SynthesizedSyllable>(),
                []);
        }

        ValidateMonophonic(plans);
        cancellation.ThrowIfCancellationRequested();

        var ids = new List<long>();
        var scorePitches = new List<float>();
        var scoreDurations = new List<float>();
        var positions = new List<long>();
        foreach (NotePlan plan in plans)
        {
            plan.PhoneStart = ids.Count;
            float notePitch = (float)MidiToFrequency(plan.Note.Pitch);
            for (int i = 0; i < plan.Symbols.Length; i++)
            {
                int id = NeutrinoPhonemes.GetId(plan.Symbols[i]);
                ids.Add(id);
                scorePitches.Add(id == NeutrinoPhonemes.Pau ? 0 : notePitch);
                scoreDurations.Add((float)Math.Max(0.001, plan.FillEnd - plan.Note.StartTime));
                positions.Add(i);
            }
            plan.PhoneCount = ids.Count - plan.PhoneStart;
        }

        long[] phonemeIds = ids.ToArray();
        float[] scorePitchesHz = scorePitches.ToArray();
        float[] scoreDurationValues = scoreDurations.ToArray();
        long[] phonePositions = positions.ToArray();
        PhoneChunk[] phoneChunks = NeutrinoTiming.BuildPhoneChunks(phonemeIds);

        double scoreOrigin = plans[0].Note.StartTime;
        double[] predictedBoundaries = NeutrinoTiming.BuildTimingBoundaries(
            scoreDurationValues,
            phonePositions,
            phoneChunks,
            FrameSeconds,
            chunk => RunTimingChunk(
                voicebank,
                chunk,
                phonemeIds,
                scorePitchesHz,
                scoreDurationValues,
                phonePositions),
            Math.Max(0, availableLeadingSeconds));
        progress?.Report(0.18);
        cancellation.ThrowIfCancellationRequested();

        BuildSyllables(plans, predictedBoundaries, scoreOrigin);
        PhonemeTiming[][] resolved = ResolvePhonemeTiming(plans);
        var finalDurations = new float[phonemeIds.Length];
        double synthesisStart = double.PositiveInfinity;
        int flatPhone = 0;
        for (int note = 0; note < plans.Count; note++)
        {
            PhonemeTiming[] timings = resolved[note];
            if (timings.Length != plans[note].PhoneCount)
                throw new InvalidDataException("TuneLab phoneme layout returned an unexpected phoneme count.");
            for (int phone = 0; phone < timings.Length; phone++, flatPhone++)
            {
                synthesisStart = Math.Min(synthesisStart, timings[phone].Start);
                finalDurations[flatPhone] = (float)Math.Max(0.001, timings[phone].Duration);
            }
        }
        if (!double.IsFinite(synthesisStart))
            synthesisStart = scoreOrigin;

        double[] normalizedBoundaries = BuildNormalizedBoundaries(finalDurations);
        int totalFrames = Math.Max(1, (int)Math.Round(normalizedBoundaries[^1] / FrameSeconds));
        long[] framePhonemeMap = NeutrinoTiming.BuildFramePhonemeMap(
            finalDurations,
            totalFrames,
            FrameSeconds);
        phoneChunks = NeutrinoTiming.BuildPhoneChunks(phonemeIds);
        FrameChunk[] frameChunks = NeutrinoTiming.BuildFrameChunks(
            phoneChunks,
            normalizedBoundaries,
            totalFrames,
            FrameSeconds);
        float[] styleShiftCents = BuildStyleShiftCents(
            snapshot,
            totalFrames,
            synthesisStart);

        float[] f0 = RunPitch(
            voicebank,
            phonemeIds,
            scorePitchesHz,
            scoreDurationValues,
            phonePositions,
            finalDurations,
            frameChunks,
            totalFrames,
            styleShiftCents,
            cancellation);
        ApplyTuneLabPitch(snapshot, f0, phonemeIds, framePhonemeMap, synthesisStart);
        IReadOnlyList<IReadOnlyList<Point>> pitchSegments = BuildPitchSegments(f0, synthesisStart);
        progress?.Report(0.38);
        cancellation.ThrowIfCancellationRequested();

        float[] waveform = RunAcousticAndVocoder(
            voicebank,
            phonemeIds,
            scorePitchesHz,
            scoreDurationValues,
            phonePositions,
            finalDurations,
            frameChunks,
            f0,
            totalFrames,
            progress,
            cancellation);

        var phonemes = new Map<string, SynthesizedSyllable>();
        foreach (NotePlan plan in plans)
            phonemes.Add(plan.Note.Id, plan.Syllable!);

        long sampleOffset = (long)Math.Round(synthesisStart * SampleRate);
        if (sampleOffset < 0)
        {
            int skip = (int)Math.Min(-sampleOffset, waveform.LongLength);
            waveform = waveform[skip..];
            sampleOffset = 0;
            pitchSegments = ClipPitchAtZero(pitchSegments);
        }
        progress?.Report(1);
        return new NeutrinoRenderedBlock(sampleOffset, waveform, phonemes, pitchSegments);
    }

    static List<NotePlan> BuildPlans(
        VoiceSynthesisSnapshot snapshot,
        IReadOnlyList<bool> continuation)
    {
        var result = new List<NotePlan>();
        for (int i = 0; i < snapshot.Notes.Count; i++)
        {
            if (continuation[i])
                continue;

            VoiceSynthesisNoteSnapshot note = snapshot.Notes[i];
            double fillEnd = note.EndTime;
            int next = i + 1;
            while (next < snapshot.Notes.Count && continuation[next])
            {
                fillEnd = Math.Max(fillEnd, snapshot.Notes[next].EndTime);
                next++;
            }

            bool pinned = note.LeadingPhonemes.Count > 0 || note.BodyPhonemes.Count > 0;
            string[] symbols = pinned
                ? note.LeadingPhonemes.Concat(note.BodyPhonemes)
                    .Select(phone => NeutrinoPhonemes.NormalizeSymbol(phone.Symbol))
                    .ToArray()
                : NeutrinoPhonemes.LyricToPhonemes(note.Lyric);
            if (symbols.Length == 0)
                symbols = ["pau"];

            result.Add(new NotePlan
            {
                SnapshotIndex = i,
                Note = note,
                FillEnd = Math.Max(note.StartTime + FrameSeconds, fillEnd),
                Symbols = symbols,
                Pinned = pinned,
            });
        }
        return result;
    }

    static void ValidateMonophonic(IReadOnlyList<NotePlan> plans)
    {
        for (int i = 1; i < plans.Count; i++)
        {
            if (plans[i].Note.StartTime + 1e-9 < plans[i - 1].FillEnd)
            {
                throw new InvalidDataException(
                    "NEUTRINO v3 is monophonic. Overlapping sung notes in one voice part are not supported.");
            }
        }
    }

    static float[] RunTimingChunk(
        NeutrinoVoicebank voicebank,
        PhoneChunk chunk,
        long[] ids,
        float[] pitches,
        float[] durations,
        long[] positions)
    {
        var electron = NamedOnnxValue.CreateFromTensor("electron",
            new DenseTensor<long>(NeutrinoTiming.Slice(ids, chunk.PhoneStart, chunk.PhoneCount), [1, chunk.PhoneCount]));
        var muon = NamedOnnxValue.CreateFromTensor("muon",
            new DenseTensor<float>(NeutrinoTiming.Slice(pitches, chunk.PhoneStart, chunk.PhoneCount), [1, chunk.PhoneCount]));
        var tau = NamedOnnxValue.CreateFromTensor("tau",
            new DenseTensor<float>(NeutrinoTiming.Slice(durations, chunk.PhoneStart, chunk.PhoneCount), [1, chunk.PhoneCount]));
        var selectron = NamedOnnxValue.CreateFromTensor("selectron",
            new DenseTensor<long>(NeutrinoTiming.Slice(positions, chunk.PhoneStart, chunk.PhoneCount), [1, chunk.PhoneCount]));
        return voicebank.RunTiming([electron, muon, tau, selectron]);
    }

    static void BuildSyllables(
        IReadOnlyList<NotePlan> plans,
        double[] boundaries,
        double scoreOrigin)
    {
        foreach (NotePlan plan in plans)
        {
            if (plan.Pinned)
            {
                var leading = plan.Note.LeadingPhonemes.Select(ToSynthesizedPhoneme).ToArray();
                var body = plan.Note.BodyPhonemes.Select(ToSynthesizedPhoneme).ToArray();
                plan.Syllable = new SynthesizedSyllable(leading, body, plan.Note.BodyOffset);
                continue;
            }

            int nucleus = Array.FindIndex(plan.Symbols, NeutrinoPhonemes.IsCoreVowel);
            if (nucleus < 0)
                nucleus = 0;

            var predicted = new SynthesizedPhoneme[plan.PhoneCount];
            for (int i = 0; i < predicted.Length; i++)
            {
                double duration = Math.Max(
                    0.001,
                    boundaries[plan.PhoneStart + i + 1] - boundaries[plan.PhoneStart + i]);
                double weight = i >= nucleus && NeutrinoPhonemes.IsCoreVowel(plan.Symbols[i]) ? 1 : 0;
                if (i == nucleus)
                    weight = 1;
                predicted[i] = new SynthesizedPhoneme
                {
                    Symbol = plan.Symbols[i],
                    Duration = duration,
                    StretchWeight = weight,
                };
            }

            double noteStartRelative = plan.Note.StartTime - scoreOrigin;
            double bodyOffset = boundaries[plan.PhoneStart + nucleus] - noteStartRelative;
            plan.Syllable = new SynthesizedSyllable(
                predicted[..nucleus],
                predicted[nucleus..],
                bodyOffset);
        }
    }

    static SynthesizedPhoneme ToSynthesizedPhoneme(VoiceSynthesisPhonemeSnapshot phone) => new()
    {
        Symbol = phone.Symbol,
        Duration = phone.Duration,
        StretchWeight = phone.StretchWeight,
    };

    static PhonemeTiming[][] ResolvePhonemeTiming(IReadOnlyList<NotePlan> plans)
    {
        var layout = new PhonemeLayoutNote[plans.Count];
        for (int i = 0; i < plans.Count; i++)
        {
            SynthesizedSyllable syllable = plans[i].Syllable!;
            layout[i] = new PhonemeLayoutNote
            {
                FillStart = plans[i].Note.StartTime,
                FillEnd = plans[i].FillEnd,
                LeadingPhonemes = syllable.LeadingPhonemes,
                BodyPhonemes = syllable.BodyPhonemes,
                BodyOffset = syllable.BodyOffset,
            };
        }
        return PhonemeLayout.Resolve(layout);
    }

    static double[] BuildNormalizedBoundaries(float[] durations)
    {
        var result = new double[durations.Length + 1];
        double time = 0;
        for (int i = 0; i < durations.Length; i++)
        {
            result[i] = time;
            time += durations[i];
        }
        result[^1] = time;
        return result;
    }

    static float[] RunPitch(
        NeutrinoVoicebank voicebank,
        long[] phonemeIds,
        float[] scorePitches,
        float[] scoreDurations,
        long[] phonePositions,
        float[] timingDurations,
        FrameChunk[] chunks,
        int totalFrames,
        float[] styleShiftCents,
        CancellationToken cancellation)
    {
        var result = new float[totalFrames];
        foreach (FrameChunk chunk in chunks)
        {
            if (!chunk.IsActive || chunk.FrameCount <= 0)
                continue;
            cancellation.ThrowIfCancellationRequested();

            long[] ids = NeutrinoTiming.Slice(phonemeIds, chunk.PhoneStart, chunk.PhoneCount);
            float[] timing = NeutrinoTiming.Slice(timingDurations, chunk.PhoneStart, chunk.PhoneCount);
            float[] pitches = NeutrinoTiming.Slice(scorePitches, chunk.PhoneStart, chunk.PhoneCount);
            float[] scores = NeutrinoTiming.Slice(scoreDurations, chunk.PhoneStart, chunk.PhoneCount);
            long[] positions = NeutrinoTiming.Slice(phonePositions, chunk.PhoneStart, chunk.PhoneCount);
            long[] frameMap = NeutrinoTiming.BuildFramePhonemeMap(timing, chunk.FrameCount, FrameSeconds);
            float[] chunkStyleShift = styleShiftCents.Length == 0
                ? []
                : NeutrinoTiming.Slice(styleShiftCents, chunk.FrameStart, chunk.FrameCount);
            pitches = ApplyStyleShiftToScorePitches(
                pitches,
                BuildPhoneStyleShiftCents(frameMap, chunk.PhoneCount, chunkStyleShift));

            var electron = NamedOnnxValue.CreateFromTensor("electron", new DenseTensor<long>(ids, [1, chunk.PhoneCount]));
            var muon = NamedOnnxValue.CreateFromTensor("muon", new DenseTensor<float>(timing, [1, chunk.PhoneCount]));
            var tau = NamedOnnxValue.CreateFromTensor("tau", new DenseTensor<float>(pitches, [1, chunk.PhoneCount]));
            var selectron = NamedOnnxValue.CreateFromTensor("selectron", new DenseTensor<float>(scores, [1, chunk.PhoneCount]));
            var smuon = NamedOnnxValue.CreateFromTensor("smuon", new DenseTensor<long>(positions, [1, chunk.PhoneCount]));
            var stau = NamedOnnxValue.CreateFromTensor("stau", new DenseTensor<long>(frameMap, [1, chunk.FrameCount]));
            float[] chunkF0 = NeutrinoTiming.RequireLength(
                voicebank.RunPitch([electron, muon, tau, selectron, smuon, stau]),
                chunk.FrameCount,
                "NEUTRINO v3 p.bin F0 output");
            ApplyInverseStyleShiftToF0(chunkF0, chunkStyleShift);
            ClampF0(chunkF0);
            Array.Copy(chunkF0, 0, result, chunk.FrameStart, chunkF0.Length);
        }
        return result;
    }

    internal static float[] BuildStyleShiftCents(
        VoiceSynthesisSnapshot snapshot,
        int totalFrames,
        double startTime)
    {
        if (totalFrames <= 0 ||
            !snapshot.Automations.TryGetValue(
                NeutrinoV3Engine.StyleShiftAutomationId,
                out var automation))
        {
            return [];
        }

        var times = new double[totalFrames];
        for (int frame = 0; frame < times.Length; frame++)
            times[frame] = startTime + frame * FrameSeconds;
        double[] sampled = automation.Evaluator.Evaluate(times);
        var result = new float[totalFrames];
        bool hasShift = false;
        for (int frame = 0; frame < result.Length; frame++)
        {
            double value = sampled[frame];
            if (!double.IsFinite(value))
                value = 0;
            result[frame] = (float)Math.Clamp(value, StyleShiftMinCents, StyleShiftMaxCents);
            hasShift |= Math.Abs(result[frame]) > 0.5f;
        }
        return hasShift ? result : [];
    }

    internal static float[] BuildPhoneStyleShiftCents(
        long[] framePhonemeMap,
        int phoneCount,
        float[] styleShiftCentsByFrame)
    {
        if (phoneCount <= 0 || styleShiftCentsByFrame.Length == 0)
            return [];

        var sums = new double[phoneCount];
        var counts = new int[phoneCount];
        int frameCount = Math.Min(framePhonemeMap.Length, styleShiftCentsByFrame.Length);
        for (int frame = 0; frame < frameCount; frame++)
        {
            int phone = Math.Clamp((int)framePhonemeMap[frame] - 1, 0, phoneCount - 1);
            sums[phone] += styleShiftCentsByFrame[frame];
            counts[phone]++;
        }

        var result = new float[phoneCount];
        for (int phone = 0; phone < phoneCount; phone++)
        {
            if (counts[phone] > 0)
                result[phone] = (float)(sums[phone] / counts[phone]);
        }
        return result;
    }

    internal static float[] ApplyStyleShiftToScorePitches(
        float[] scorePitches,
        float[] phoneStyleShiftCents)
    {
        if (phoneStyleShiftCents.Length == 0)
            return scorePitches;

        var result = (float[])scorePitches.Clone();
        for (int phone = 0; phone < result.Length && phone < phoneStyleShiftCents.Length; phone++)
        {
            if (result[phone] > 0 && Math.Abs(phoneStyleShiftCents[phone]) > 0.5f)
                result[phone] *= StyleShiftFactor(phoneStyleShiftCents[phone]);
        }
        return result;
    }

    internal static void ApplyInverseStyleShiftToF0(float[] f0, float[] styleShiftCentsByFrame)
    {
        for (int frame = 0; frame < f0.Length && frame < styleShiftCentsByFrame.Length; frame++)
        {
            if (f0[frame] > 0 && Math.Abs(styleShiftCentsByFrame[frame]) > 0.5f)
                f0[frame] /= StyleShiftFactor(styleShiftCentsByFrame[frame]);
        }
    }

    static float StyleShiftFactor(float cents) =>
        (float)Math.Pow(2.0, cents / 1200.0);

    static void ApplyTuneLabPitch(
        VoiceSynthesisSnapshot snapshot,
        float[] f0,
        long[] phonemeIds,
        long[] framePhonemeMap,
        double startTime)
    {
        var times = new double[f0.Length];
        for (int frame = 0; frame < times.Length; frame++)
            times[frame] = startTime + frame * FrameSeconds;
        double[] absolute = snapshot.Pitch.Evaluator.Evaluate(times);
        double[] deviation = snapshot.PitchDeviation.Evaluator.Evaluate(times);
        for (int frame = 0; frame < f0.Length; frame++)
        {
            int phone = Math.Clamp((int)framePhonemeMap[frame] - 1, 0, phonemeIds.Length - 1);
            if (phonemeIds[phone] == NeutrinoPhonemes.Pau || f0[frame] < F0Min)
            {
                f0[frame] = 0;
                continue;
            }

            double pitch = double.IsNaN(absolute[frame])
                ? FrequencyToMidi(f0[frame])
                : absolute[frame];
            if (double.IsFinite(deviation[frame]))
                pitch += deviation[frame];
            double frequency = MidiToFrequency(pitch);
            f0[frame] = double.IsFinite(frequency) ? (float)frequency : 0;
        }
        ClampF0(f0);
    }

    static IReadOnlyList<IReadOnlyList<Point>> BuildPitchSegments(float[] f0, double startTime)
    {
        var result = new List<IReadOnlyList<Point>>();
        List<Point>? current = null;
        for (int frame = 0; frame < f0.Length; frame++)
        {
            if (f0[frame] < F0Min)
            {
                if (current is { Count: > 0 })
                    result.Add(current);
                current = null;
                continue;
            }
            current ??= [];
            current.Add(new Point(startTime + frame * FrameSeconds, FrequencyToMidi(f0[frame])));
        }
        if (current is { Count: > 0 })
            result.Add(current);
        return result;
    }

    static IReadOnlyList<IReadOnlyList<Point>> ClipPitchAtZero(
        IReadOnlyList<IReadOnlyList<Point>> segments)
    {
        var result = new List<IReadOnlyList<Point>>();
        foreach (var segment in segments)
        {
            var kept = segment.Where(point => point.X >= 0).ToArray();
            if (kept.Length > 0)
                result.Add(kept);
        }
        return result;
    }

    static float[] RunAcousticAndVocoder(
        NeutrinoVoicebank voicebank,
        long[] phonemeIds,
        float[] scorePitches,
        float[] scoreDurations,
        long[] phonePositions,
        float[] timingDurations,
        FrameChunk[] chunks,
        float[] f0,
        int totalFrames,
        IProgress<double>? progress,
        CancellationToken cancellation)
    {
        var waveform = new float[totalFrames * HopSize];
        int activeCount = Math.Max(1, chunks.Count(chunk => chunk.IsActive && chunk.FrameCount > 0));
        int completed = 0;
        foreach (FrameChunk chunk in chunks)
        {
            if (!chunk.IsActive || chunk.FrameCount <= 0)
                continue;
            cancellation.ThrowIfCancellationRequested();

            long[] ids = NeutrinoTiming.Slice(phonemeIds, chunk.PhoneStart, chunk.PhoneCount);
            float[] timing = NeutrinoTiming.Slice(timingDurations, chunk.PhoneStart, chunk.PhoneCount);
            float[] pitches = NeutrinoTiming.Slice(scorePitches, chunk.PhoneStart, chunk.PhoneCount);
            float[] scores = NeutrinoTiming.Slice(scoreDurations, chunk.PhoneStart, chunk.PhoneCount);
            long[] positions = NeutrinoTiming.Slice(phonePositions, chunk.PhoneStart, chunk.PhoneCount);
            long[] frameMap = NeutrinoTiming.BuildFramePhonemeMap(timing, chunk.FrameCount, FrameSeconds);
            float[] chunkF0 = NeutrinoTiming.Slice(f0, chunk.FrameStart, chunk.FrameCount);

            var electron = NamedOnnxValue.CreateFromTensor("electron", new DenseTensor<long>(ids, [1, chunk.PhoneCount]));
            var muon = NamedOnnxValue.CreateFromTensor("muon", new DenseTensor<float>(timing, [1, chunk.PhoneCount]));
            var tau = NamedOnnxValue.CreateFromTensor("tau", new DenseTensor<float>(pitches, [1, chunk.PhoneCount]));
            var selectron = NamedOnnxValue.CreateFromTensor("selectron", new DenseTensor<float>(scores, [1, chunk.PhoneCount]));
            var smuon = NamedOnnxValue.CreateFromTensor("smuon", new DenseTensor<long>(positions, [1, chunk.PhoneCount]));
            var stau = NamedOnnxValue.CreateFromTensor("stau", new DenseTensor<long>(frameMap, [1, chunk.FrameCount]));
            var photon = NamedOnnxValue.CreateFromTensor("photon", new DenseTensor<float>(chunkF0, [1, chunk.FrameCount]));
            float[] mel = NeutrinoTiming.RequireLength(
                voicebank.RunMelspec([electron, muon, tau, selectron, smuon, stau, photon]),
                chunk.FrameCount * MelBins,
                "NEUTRINO v3 s.bin mel output");
            ClampMelspec(mel);
            cancellation.ThrowIfCancellationRequested();

            var vocoderInput = new float[chunk.FrameCount * (MelBins + 1)];
            for (int frame = 0; frame < chunk.FrameCount; frame++)
            {
                Array.Copy(mel, frame * MelBins, vocoderInput, frame * (MelBins + 1), MelBins);
                vocoderInput[frame * (MelBins + 1) + MelBins] = chunkF0[frame];
            }
            var input = NamedOnnxValue.CreateFromTensor("input",
                new DenseTensor<float>(vocoderInput, [1, chunk.FrameCount, MelBins + 1]));
            float[] chunkWaveform = NeutrinoTiming.RequireLength(
                voicebank.RunVocoder([input]),
                chunk.FrameCount * HopSize,
                "NEUTRINO v3 v.bin waveform output");
            PostProcessWaveform(chunkWaveform);
            Array.Copy(chunkWaveform, 0, waveform, chunk.FrameStart * HopSize, chunkWaveform.Length);
            completed++;
            progress?.Report(0.38 + 0.60 * completed / activeCount);
        }
        return waveform;
    }

    static void ClampF0(float[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (!float.IsFinite(values[i]) || values[i] < F0Min)
                values[i] = 0;
            else if (values[i] > F0Max)
                values[i] = F0Max;
        }
    }

    static void ClampMelspec(float[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (!float.IsFinite(values[i]))
                values[i] = MelspecMin;
            else
                values[i] = Math.Clamp(values[i], MelspecMin, MelspecMax);
        }
    }

    static void PostProcessWaveform(float[] waveform)
    {
        int edge = Math.Min(EdgeSilenceSamples, waveform.Length / 2);
        for (int i = 0; i < edge; i++)
        {
            waveform[i] = 0;
            waveform[waveform.Length - 1 - i] = 0;
        }
        int fadeIn = Math.Min(FadeInSamples, Math.Max(0, waveform.Length - edge));
        for (int i = 0; i < fadeIn; i++)
        {
            int index = edge + i;
            if (index >= waveform.Length)
                break;
            waveform[index] *= (float)Math.Pow((double)i / FadeInSamples, 2);
        }
        int fadeOut = Math.Min(FadeOutSamples, Math.Max(0, waveform.Length - edge));
        for (int i = 0; i < fadeOut; i++)
        {
            int index = waveform.Length - edge - 1 - i;
            if (index < 0)
                break;
            waveform[index] *= (float)Math.Pow((double)i / FadeOutSamples, 2);
        }
        for (int i = 0; i < waveform.Length; i++)
        {
            float value = waveform[i] * WaveScale;
            if (!float.IsFinite(value))
                value = 0;
            waveform[i] = Math.Clamp(value, -WaveClamp, WaveClamp);
        }
    }

    static double MidiToFrequency(double midi) => 440.0 * Math.Pow(2, (midi - 69.0) / 12.0);
    static double FrequencyToMidi(double frequency) => 69.0 + 12.0 * Math.Log2(frequency / 440.0);

    sealed class NotePlan
    {
        public required int SnapshotIndex;
        public required VoiceSynthesisNoteSnapshot Note;
        public required double FillEnd;
        public required string[] Symbols;
        public required bool Pinned;
        public int PhoneStart;
        public int PhoneCount;
        public SynthesizedSyllable? Syllable;
    }
}
