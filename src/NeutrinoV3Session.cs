using TuneLab.Foundation;
using TuneLab.SDK;

namespace TuneLab.NeutrinoV3;

internal sealed class NeutrinoV3Session : IVoiceSynthesisSession
{
    public NeutrinoV3Session(IVoiceSynthesisContext context, NeutrinoVoicebank voicebank)
    {
        mContext = context;
        mVoicebank = voicebank;

        context.Notes.WhenAnyItem(
                note => note.StartTime.Modified,
                note => note.EndTime.Modified,
                note => note.Pitch.Modified,
                note => note.Lyric.Modified,
                note => note.LeadingPhonemes.Modified,
                note => note.BodyPhonemes.Modified,
                note => note.BodyOffset.Modified,
                note => note.Properties.Modified)
            .Subscribe(OnNoteChanged, mSubscriptions);
        context.Notes.ItemAdded.Subscribe(OnNotesChanged, mSubscriptions);
        context.Notes.ItemRemoved.Subscribe(OnNotesChanged, mSubscriptions);
        context.PartProperties.Modified.Subscribe(MarkAllDirtyAndResegment, mSubscriptions);
        context.Committed.Subscribe(OnCommitted);
        context.Pitch.RangeModified.Subscribe(OnPitchChanged);
        context.PitchDeviation.RangeModified.Subscribe(OnPitchChanged);
        if (context.Automations.TryGetValue(NeutrinoV3Engine.StyleShiftAutomationId, out var styleShift))
        {
            mStyleShift = styleShift;
            mStyleShift.RangeModified.Subscribe(OnPitchChanged);
        }
        mNeedResegment = true;
    }

    public string DefaultLyric => "あ";

    public bool IsContinuation(IVoiceSynthesisNote note)
    {
        if (!NeutrinoPhonemes.IsContinuationLyric(note.Lyric.Value) || HasPinnedPhonemes(note))
            return false;

        IVoiceSynthesisNote current = note;
        while (true)
        {
            IVoiceSynthesisNote? previous = current.Previous;
            if (previous is null || previous.EndTime.Value < current.StartTime.Value)
                return false;
            if (!NeutrinoPhonemes.IsContinuationLyric(previous.Lyric.Value) || HasPinnedPhonemes(previous))
                return true;
            current = previous;
        }
    }

    public SynthesisRange? GetNextPendingSynthesisRange(double startTime, double endTime)
    {
        Piece? piece = FindNextDirtyPiece(startTime, endTime);
        return piece is null ? null : new SynthesisRange(piece.StartTime, piece.EndTime);
    }

    public async Task SynthesizeNext(
        double startTime,
        double endTime,
        CancellationToken cancellation = default)
    {
        Piece? piece = FindNextDirtyPiece(startTime, endTime);
        if (piece is null)
            return;

        VoiceSynthesisSnapshot snapshot = mContext.GetSnapshot(piece.Notes);
        bool[] continuation = piece.Notes.Select(IsContinuation).ToArray();
        long generation = piece.Generation;
        piece.Dirty = false;
        piece.Synthesizing = true;
        piece.Progress = 0;
        piece.Error = null;
        NotifyAll();

        var progress = new Progress<double>(value =>
        {
            if (!mDisposed && mPieces.Contains(piece))
            {
                piece.Progress = Math.Clamp(value, 0, 1);
                mStatusChanged.Invoke();
            }
        });

        try
        {
            NeutrinoRenderedBlock? rendered = await Task.Run(
                () => NeutrinoSynthesis.Render(
                    mVoicebank,
                    snapshot,
                    continuation,
                    piece.AvailableLeadingSeconds,
                    progress,
                    cancellation),
                CancellationToken.None);

            if (rendered is null || mDisposed || !mPieces.Contains(piece) || piece.Generation != generation)
                return;

            piece.Segment?.Dispose();
            piece.Segment = null;
            if (rendered.Audio.Length > 0)
            {
                piece.Segment = mContext.CreateAudioSegment(
                    rendered.SampleOffset,
                    rendered.Audio.Length,
                    NeutrinoSynthesis.SampleRate);
                piece.Segment.Write(0, rendered.Audio);
                piece.Segment.Commit();
            }
            piece.Phonemes = rendered.Phonemes;
            piece.PitchSegments = rendered.PitchSegments;
            piece.Failed = false;
        }
        catch (OperationCanceledException)
        {
            if (!mDisposed && mPieces.Contains(piece))
                piece.Dirty = true;
        }
        catch (Exception exception)
        {
            if (!mDisposed && mPieces.Contains(piece))
            {
                piece.Failed = true;
                piece.Dirty = false;
                piece.Error = exception.ToString();
            }
        }
        finally
        {
            if (!mDisposed && mPieces.Contains(piece))
            {
                piece.Synthesizing = false;
                NotifyAll();
            }
        }
    }

    public SynthesizedPitch SynthesizedPitch
    {
        get
        {
            var segments = new List<IReadOnlyList<Point>>();
            foreach (Piece piece in mPieces)
            {
                if (piece.Dirty || piece.Synthesizing || piece.Failed || piece.Segment is null)
                    continue;
                segments.AddRange(piece.PitchSegments);
            }
            return new SynthesizedPitch { Segments = segments };
        }
    }

    public IReadOnlyMap<string, SynthesizedParameter> SynthesizedParameters =>
        new Map<string, SynthesizedParameter>();

    public IReadOnlyMap<string, SynthesizedSyllable> SynthesizedPhonemes
    {
        get
        {
            var result = new Map<string, SynthesizedSyllable>();
            foreach (Piece piece in mPieces)
            {
                if (piece.Dirty || piece.Synthesizing || piece.Failed || piece.Segment is null)
                    continue;
                foreach (var pair in piece.Phonemes)
                    result.Add(pair.Key, pair.Value);
            }
            return result;
        }
    }

    public IReadOnlyList<SynthesisStatusSegment> Status
    {
        get
        {
            var result = new List<SynthesisStatusSegment>(mPieces.Count);
            foreach (Piece piece in mPieces)
            {
                SynthesisSegmentStatus status = piece.Failed
                    ? SynthesisSegmentStatus.Failed
                    : piece.Synthesizing
                        ? SynthesisSegmentStatus.Synthesizing
                        : piece.Dirty || piece.Segment is null
                            ? SynthesisSegmentStatus.Pending
                            : SynthesisSegmentStatus.Synthesized;
                result.Add(new SynthesisStatusSegment
                {
                    StartTime = piece.StartTime,
                    EndTime = piece.EndTime,
                    Status = status,
                    Message = piece.Failed ? piece.Error : piece.Synthesizing ? "NEUTRINO v3 CPU" : null,
                    Progress = piece.Synthesizing ? piece.Progress : 0,
                });
            }
            return result;
        }
    }

    public IActionEvent SynthesizedPhonemesChanged => mPhonemesChanged;
    public IActionEvent SynthesizedParametersChanged => mParametersChanged;
    public IActionEvent SynthesizedPitchChanged => mPitchChanged;
    public IActionEvent StatusChanged => mStatusChanged;

    public void Dispose()
    {
        if (mDisposed)
            return;
        mDisposed = true;
        mSubscriptions.DisposeAll();
        mContext.Committed.Unsubscribe(OnCommitted);
        mContext.Pitch.RangeModified.Unsubscribe(OnPitchChanged);
        mContext.PitchDeviation.RangeModified.Unsubscribe(OnPitchChanged);
        mStyleShift?.RangeModified.Unsubscribe(OnPitchChanged);
        foreach (Piece piece in mPieces)
            piece.Segment?.Dispose();
        mPieces.Clear();
        mVoicebank.Release();
    }

    Piece? FindNextDirtyPiece(double startTime, double endTime)
    {
        if (mNeedResegment)
            Resegment();
        foreach (Piece piece in mPieces)
        {
            if (!piece.Dirty || piece.Failed || piece.Synthesizing)
                continue;
            if (piece.EndTime < startTime || piece.StartTime > endTime)
                continue;
            return piece;
        }
        return null;
    }

    void Resegment()
    {
        mNeedResegment = false;
        var groups = new List<Group>();
        List<IVoiceSynthesisNote>? current = null;
        double currentMaxEnd = 0;
        double previousGroupEnd = 0;
        foreach (IVoiceSynthesisNote note in mContext.Notes)
        {
            if (current is null || note.StartTime.Value > currentMaxEnd)
            {
                if (current is not null)
                {
                    groups.Add(new Group(current, currentMaxEnd, previousGroupEnd));
                    previousGroupEnd = currentMaxEnd;
                }
                current = [];
                currentMaxEnd = note.EndTime.Value;
            }
            else
            {
                currentMaxEnd = Math.Max(currentMaxEnd, note.EndTime.Value);
            }
            current.Add(note);
        }
        if (current is not null)
            groups.Add(new Group(current, currentMaxEnd, previousGroupEnd));

        var nextPieces = new List<Piece>(groups.Count);
        foreach (Group group in groups)
        {
            Piece? existing = mPieces.FirstOrDefault(piece => piece.Notes.SequenceEqual(group.Notes));
            double start = group.Notes[0].StartTime.Value;
            double availableLeading = Math.Min(0.5, Math.Max(0, start - group.PreviousEnd));
            if (existing is not null)
            {
                mPieces.Remove(existing);
                existing.StartTime = start;
                existing.EndTime = group.End;
                existing.AvailableLeadingSeconds = availableLeading;
                nextPieces.Add(existing);
            }
            else
            {
                nextPieces.Add(new Piece
                {
                    Notes = group.Notes,
                    StartTime = start,
                    EndTime = group.End,
                    AvailableLeadingSeconds = availableLeading,
                    Dirty = true,
                    Generation = 1,
                });
            }
        }

        foreach (Piece removed in mPieces)
            removed.Segment?.Dispose();
        mPieces.Clear();
        mPieces.AddRange(nextPieces);
        NotifyAll();
    }

    void OnNoteChanged(IVoiceSynthesisNote note) => MarkAllDirtyAndResegment();
    void OnNotesChanged(IVoiceSynthesisNote note) => MarkAllDirtyAndResegment();

    void MarkAllDirtyAndResegment()
    {
        foreach (Piece piece in mPieces)
            MarkDirty(piece);
        mNeedResegment = true;
        NotifyAll();
    }

    void OnPitchChanged(double startTime, double endTime)
    {
        foreach (Piece piece in mPieces)
        {
            double synthesisStart = Math.Max(0, piece.StartTime - piece.AvailableLeadingSeconds);
            if (piece.EndTime < startTime || synthesisStart > endTime)
                continue;
            MarkDirty(piece);
        }
        NotifyAll();
    }

    void MarkDirty(Piece piece)
    {
        piece.Dirty = true;
        piece.Failed = false;
        piece.Error = null;
        piece.Generation++;
    }

    void OnCommitted()
    {
        if (mNeedResegment)
            Resegment();
    }

    void NotifyAll()
    {
        mPhonemesChanged.Invoke();
        mParametersChanged.Invoke();
        mPitchChanged.Invoke();
        mStatusChanged.Invoke();
    }

    static bool HasPinnedPhonemes(IVoiceSynthesisNote note) =>
        note.LeadingPhonemes.Value.Count > 0 || note.BodyPhonemes.Value.Count > 0;

    sealed record Group(List<IVoiceSynthesisNote> Notes, double End, double PreviousEnd);

    sealed class Piece
    {
        public required IReadOnlyList<IVoiceSynthesisNote> Notes;
        public double StartTime;
        public double EndTime;
        public double AvailableLeadingSeconds;
        public bool Dirty;
        public bool Synthesizing;
        public bool Failed;
        public long Generation;
        public double Progress;
        public string? Error;
        public IAudioSegment? Segment;
        public IReadOnlyMap<string, SynthesizedSyllable> Phonemes = new Map<string, SynthesizedSyllable>();
        public IReadOnlyList<IReadOnlyList<Point>> PitchSegments = [];
    }

    readonly IVoiceSynthesisContext mContext;
    readonly NeutrinoVoicebank mVoicebank;
    readonly ISynthesisAutomation? mStyleShift;
    readonly DisposableManager mSubscriptions = new();
    readonly List<Piece> mPieces = [];
    readonly ActionEvent mPhonemesChanged = new();
    readonly ActionEvent mParametersChanged = new();
    readonly ActionEvent mPitchChanged = new();
    readonly ActionEvent mStatusChanged = new();
    bool mNeedResegment;
    bool mDisposed;
}
