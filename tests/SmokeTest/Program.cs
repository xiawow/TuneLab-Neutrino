using TuneLab.Foundation;
using TuneLab.NeutrinoV3;
using TuneLab.SDK;

string root = args.Length > 0 ? args[0] : @"D:\NEUTRINO";
string model = args.Length > 1 ? args[1] : "MERROW";
string modelDirectory = Path.Combine(root, "model", model);

NeutrinoPhonemes.LoadDictionary(Path.Combine(root, "settings", "dic", "japanese.utf_8.table"));
using var voicebank = new NeutrinoVoicebank
{
    Id = model,
    Name = model,
    Description = "smoke test",
    ModelDirectory = modelDirectory,
    BottomKey = 41,
    TopKey = 86,
};

if (args.Skip(2).Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
    RunSelfTests(root, voicebank);
    return;
}

string lyric = args.Length > 2 ? args[2] : "あ";
VoiceSynthesisSnapshot snapshot = CreateSnapshot([
    CreateNote("note-1", 0.5, 1.5, lyric),
]);
NeutrinoRenderedBlock rendered = Render(voicebank, snapshot, [false], 0.5);
RequireSingingAudio(rendered, lyric);
PrintResult(rendered, "note-1");
WritePcm16Wave(
    Path.Combine(AppContext.BaseDirectory, $"{model}-{lyric}.wav"),
    rendered.Audio,
    NeutrinoSynthesis.SampleRate);

static void RunSelfTests(string root, NeutrinoVoicebank voicebank)
{
    Console.WriteLine("Running NEUTRINO v3 boundary smoke tests...");

    NeutrinoRenderedBlock startAtZero = Render(
        voicebank,
        CreateSnapshot([CreateNote("zero", 0, 1, "か")]),
        [false],
        0);
    RequireSingingAudio(startAtZero, "note at zero");
    Require(startAtZero.SampleOffset == 0, "A note at time zero must not produce a negative audio offset.");
    Require(
        startAtZero.PitchSegments.SelectMany(segment => segment).All(point => point.X >= 0),
        "Pitch output must not contain points before time zero.");

    NeutrinoRenderedBlock leadingConsonant = Render(
        voicebank,
        CreateSnapshot([CreateNote("lead", 0.5, 1.5, "か")]),
        [false],
        0.5);
    RequireSingingAudio(leadingConsonant, "leading consonant");
    Require(
        leadingConsonant.SampleOffset >= 0 && leadingConsonant.SampleOffset < 0.5 * NeutrinoSynthesis.SampleRate,
        "A first consonant should use available blank time before the note.");

    VoiceSynthesisSnapshot continuationSnapshot = CreateSnapshot([
        CreateNote("head", 0.5, 1, "か"),
        CreateNote("tail", 1, 1.5, "+~"),
    ]);
    NeutrinoRenderedBlock continuation = Render(
        voicebank,
        continuationSnapshot,
        [false, true],
        0.5);
    RequireSingingAudio(continuation, "continuation note");
    Require(continuation.Phonemes.ContainsKey("head"), "The head note must own the continued syllable.");
    Require(!continuation.Phonemes.ContainsKey("tail"), "A continuation note must not publish a second syllable.");
    Require(
        continuation.Audio.Length >= (int)(0.8 * NeutrinoSynthesis.SampleRate),
        "A continuation note must extend the rendered phrase instead of being skipped.");

    NeutrinoRenderedBlock rest = Render(
        voicebank,
        CreateSnapshot([CreateNote("rest", 0.5, 1, "R")]),
        [false],
        0.5);
    Require(rest.Audio.Length > 0, "A rest must retain its timeline duration.");
    Require(rest.Audio.All(sample => sample == 0), "A rest must render as exact silence.");
    Require(rest.PitchSegments.Count == 0, "A rest must not publish pitch points.");

    VoiceSynthesisSnapshot restBetweenNotesSnapshot = CreateSnapshot([
        CreateNote("before-rest", 0.2, 0.7, "あ"),
        CreateNote("middle-rest", 0.7, 1.2, "R"),
        CreateNote("after-rest", 1.2, 1.7, "い"),
    ]);
    NeutrinoRenderedBlock restBetweenNotes = Render(
        voicebank,
        restBetweenNotesSnapshot,
        [false, false, false],
        0.2);
    RequireSingingAudio(restBetweenNotes, "notes around a rest");
    int silentStart = checked((int)(Math.Round(0.85 * NeutrinoSynthesis.SampleRate) - restBetweenNotes.SampleOffset));
    int silentEnd = checked((int)(Math.Round(1.05 * NeutrinoSynthesis.SampleRate) - restBetweenNotes.SampleOffset));
    Require(silentStart >= 0 && silentEnd <= restBetweenNotes.Audio.Length, "The rest test window is outside rendered audio.");
    Require(
        restBetweenNotes.Audio[silentStart..silentEnd].All(sample => sample == 0),
        "A rest between sung notes must stay silent without model noise.");
    Require(
        restBetweenNotes.Phonemes.ContainsKey("after-rest"),
        "A sung note after a rest must still be rendered.");

    var leading = new VoiceSynthesisPhonemeSnapshot(
        "k", 0.2, 0, PropertyObject.Empty);
    var body = new VoiceSynthesisPhonemeSnapshot(
        "a", 0.2, 1, PropertyObject.Empty);
    var pinned = new VoiceSynthesisNoteSnapshot
    {
        Id = "pinned",
        StartTime = 0,
        EndTime = 1,
        Pitch = 60,
        Lyric = "か",
        LeadingPhonemes = [leading],
        BodyPhonemes = [body],
        BodyOffset = 0,
        Properties = PropertyObject.Empty,
    };
    NeutrinoRenderedBlock pinnedAtZero = Render(
        voicebank,
        CreateSnapshot([pinned]),
        [false],
        0);
    RequireSingingAudio(pinnedAtZero, "pinned phonemes at zero");
    Require(pinnedAtZero.SampleOffset == 0, "Pinned leading phonemes must be clipped at time zero.");
    Require(
        pinnedAtZero.PitchSegments.SelectMany(segment => segment).All(point => point.X >= 0),
        "Pinned phoneme pitch output must be clipped at time zero.");

    IReadOnlyList<VoiceSynthesisNoteSnapshot> styleNotes = [
        CreateNote("style", 0.5, 1.5, "あ"),
    ];
    float[] missingStyle = NeutrinoSynthesis.BuildStyleShiftCents(
        CreateSnapshot(styleNotes), 4, 0.5);
    float[] zeroStyle = NeutrinoSynthesis.BuildStyleShiftCents(
        CreateSnapshot(styleNotes, styleShiftCents: 0), 4, 0.5);
    Require(
        missingStyle.Length == 0 && zeroStyle.Length == 0,
        "A missing or zero SHFC track must preserve the previous p.bin input path.");

    float[] frameStyle = [600, 600, -600, -600];
    float[] phoneStyle = NeutrinoSynthesis.BuildPhoneStyleShiftCents(
        [1, 1, 2, 2], 2, frameStyle);
    Require(
        phoneStyle.SequenceEqual(new float[] { 600, -600 }),
        "SHFC frame values must be averaged onto the matching phonemes.");
    float[] shiftedScore = NeutrinoSynthesis.ApplyStyleShiftToScorePitches(
        [100, 200], phoneStyle);
    Require(
        Math.Abs(shiftedScore[0] - 141.42136f) < 1e-4 &&
        Math.Abs(shiftedScore[1] - 141.42136f) < 1e-4,
        "SHFC must shift the score pitch supplied to p.bin.");
    float[] compensatedF0 = (float[])shiftedScore.Clone();
    NeutrinoSynthesis.ApplyInverseStyleShiftToF0(compensatedF0, [600, -600]);
    Require(
        Math.Abs(compensatedF0[0] - 100) < 1e-4 &&
        Math.Abs(compensatedF0[1] - 200) < 1e-4,
        "SHFC must restore the final F0 after p.bin inference.");

    NeutrinoRenderedBlock shiftedStyle = Render(
        voicebank,
        CreateSnapshot(styleNotes, styleShiftCents: 600),
        [false],
        0.5);
    RequireSingingAudio(shiftedStyle, "SHFC style shift");
    double[] shiftedPitch = shiftedStyle.PitchSegments.SelectMany(segment => segment).Select(point => point.Y).ToArray();
    Require(shiftedPitch.Length > 0, "The SHFC test needs voiced pitch points.");
    double meanPitchDifference = shiftedPitch.Average() - 60;
    Require(
        Math.Abs(meanPitchDifference) < 2,
        $"SHFC must compensate transposition; mean pitch is {meanPitchDifference:F2} semitones from the note.");
    Console.WriteLine(
        $"SHFC +600 meanPitchDeltaFromNote={meanPitchDifference:F3} st");

    bool rejectedInvalidRoot = false;
    try
    {
        NeutrinoVoicebankLocator.ResolveRoot(Path.Combine(root, "missing-neutrino-root"));
    }
    catch (DirectoryNotFoundException)
    {
        rejectedInvalidRoot = true;
    }
    Require(rejectedInvalidRoot, "An invalid configured root must not silently fall back to another installation.");

    Console.WriteLine("All NEUTRINO v3 boundary smoke tests passed.");
}

static VoiceSynthesisNoteSnapshot CreateNote(
    string id,
    double start,
    double end,
    string lyric) => new()
    {
        Id = id,
        StartTime = start,
        EndTime = end,
        Pitch = 60,
        Lyric = lyric,
        LeadingPhonemes = [],
        BodyPhonemes = [],
        BodyOffset = 0,
        Properties = PropertyObject.Empty,
    };

static VoiceSynthesisSnapshot CreateSnapshot(
    IReadOnlyList<VoiceSynthesisNoteSnapshot> notes,
    double? styleShiftCents = null)
{
    var automations = new Map<string, SynthesisAutomationSnapshot>();
    if (styleShiftCents.HasValue)
    {
        automations.Add(
            NeutrinoV3Engine.StyleShiftAutomationId,
            new SynthesisAutomationSnapshot
            {
                Evaluator = new ConstantEvaluator(styleShiftCents.Value),
            });
    }
    return new VoiceSynthesisSnapshot
    {
        Notes = notes,
        Pitch = new SynthesisAutomationSnapshot { Evaluator = new ConstantEvaluator(double.NaN) },
        PitchDeviation = new SynthesisAutomationSnapshot { Evaluator = new ConstantEvaluator(0) },
        PartProperties = PropertyObject.Empty,
        Automations = automations,
    };
}

static NeutrinoRenderedBlock Render(
    NeutrinoVoicebank voicebank,
    VoiceSynthesisSnapshot snapshot,
    IReadOnlyList<bool> continuation,
    double availableLeadingSeconds) =>
    NeutrinoSynthesis.Render(
        voicebank,
        snapshot,
        continuation,
        availableLeadingSeconds,
        null,
        CancellationToken.None) ?? throw new Exception("Rendering was cancelled.");

static void RequireSingingAudio(NeutrinoRenderedBlock rendered, string scenario)
{
    Require(rendered.Audio.Length > 0, $"{scenario}: renderer returned no audio.");
    Require(rendered.Audio.Any(sample => Math.Abs(sample) > 1e-5f), $"{scenario}: renderer returned only silence.");
    Require(rendered.Audio.All(float.IsFinite), $"{scenario}: renderer returned non-finite samples.");
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

static void PrintResult(NeutrinoRenderedBlock rendered, string noteId)
{
    double peak = rendered.Audio.Max(sample => Math.Abs(sample));
    double rms = Math.Sqrt(rendered.Audio.Average(sample => (double)sample * sample));
    Console.WriteLine($"samples={rendered.Audio.Length} offset={rendered.SampleOffset} peak={peak:F6} rms={rms:F6}");
    Console.WriteLine($"phonemes={string.Join(' ', rendered.Phonemes[noteId].LeadingPhonemes.Concat(rendered.Phonemes[noteId].BodyPhonemes).Select(phone => phone.Symbol))}");
    Console.WriteLine($"pitchSegments={rendered.PitchSegments.Count} pitchPoints={rendered.PitchSegments.Sum(segment => segment.Count)}");
}

static void WritePcm16Wave(string path, float[] samples, int sampleRate)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    int dataSize = samples.Length * sizeof(short);
    writer.Write("RIFF"u8.ToArray());
    writer.Write(36 + dataSize);
    writer.Write("WAVE"u8.ToArray());
    writer.Write("fmt "u8.ToArray());
    writer.Write(16);
    writer.Write((short)1);
    writer.Write((short)1);
    writer.Write(sampleRate);
    writer.Write(sampleRate * sizeof(short));
    writer.Write((short)sizeof(short));
    writer.Write((short)16);
    writer.Write("data"u8.ToArray());
    writer.Write(dataSize);
    foreach (float sample in samples)
        writer.Write((short)Math.Round(Math.Clamp(sample, -1, 1) * short.MaxValue));
}

sealed class ConstantEvaluator(double value) : IAutomationEvaluator
{
    public void Evaluate(IReadOnlyList<double> positions, Span<double> results)
    {
        results.Fill(value);
    }
}
