using System.Runtime.InteropServices;
using Iutq.Core;
using Xunit;

namespace Iutq.Tests;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TestClip
{
    public int Value;

    public TestClip(int value)
    {
        Value = value;
    }
}

public static class TestTypes
{
    public static readonly ClipType<TestClip> Clip = new(0x7000000000000001UL);
}

public struct TransitionRecorder : IClipTransitionVisitor<TestClip>
{
    public int ClipEnter;
    public int ClipExit;
    public int BlendEnter;
    public int BlendExit;
    public int Value;

    public void OnClipTransition(in TrackInstance track, in ClipTransition transition, in TestClip clip)
    {
        if (transition.Phase == ClipPhase.Enter)
        {
            ClipEnter++;
            Value += clip.Value;
        }
        else if (transition.Phase == ClipPhase.Exit)
        {
            ClipExit++;
        }
    }

    public void OnBlendTransition(
        in TrackInstance track,
        in BlendTransition transition,
        in TestClip clipA,
        in TestClip clipB)
    {
        if (transition.Phase == BlendPhase.Enter)
            BlendEnter++;
        else if (transition.Phase == BlendPhase.Exit) BlendExit++;
    }
}

public struct OccurrenceRecorder : IClipTransitionVisitor<TestClip>
{
    public long FirstGlobalTick;
    public long SecondGlobalTick;
    public ClipPhase FirstPhase;
    public int Count;

    public void OnClipTransition(in TrackInstance track, in ClipTransition transition, in TestClip clip)
    {
        if (Count == 0)
        {
            FirstGlobalTick = transition.GlobalTick;
            FirstPhase = transition.Phase;
        }
        else if (Count == 1)
        {
            SecondGlobalTick = transition.GlobalTick;
        }

        Count++;
    }

    public void OnBlendTransition(
        in TrackInstance track,
        in BlendTransition transition,
        in TestClip clipA,
        in TestClip clipB)
    {
        Count++;
    }
}

public sealed class TimelineQueryTests
{
    [Fact]
    public void FiveFramePhases()
    {
        var db = BuildSingleTrack(
            32,
            TrackMode.Exclusive,
            Clip.Range(10, 15, new TestClip(1)));

        var handle = db.Resolve(TestTypes.Clip);
        var view = db.AsView();
        var query = view.Query(handle);
        var track = query.Tracks(new TimelineIndex(0))[0];

        Assert.Equal(ClipPhase.Enter, First(query.VisitFrames(in track, 10, TimelineDirection.Forward)).Phase);
        Assert.Equal(ClipPhase.Active, First(query.VisitFrames(in track, 11, TimelineDirection.Forward)).Phase);
        Assert.Equal(ClipPhase.Active, First(query.VisitFrames(in track, 13, TimelineDirection.Forward)).Phase);
        Assert.Equal(ClipPhase.Exit, First(query.VisitFrames(in track, 14, TimelineDirection.Forward)).Phase);

        Assert.Equal(ClipPhase.Enter, First(query.VisitFrames(in track, 14, TimelineDirection.Reverse)).Phase);
        Assert.Equal(ClipPhase.Active, First(query.VisitFrames(in track, 13, TimelineDirection.Reverse)).Phase);
        Assert.Equal(ClipPhase.Active, First(query.VisitFrames(in track, 11, TimelineDirection.Reverse)).Phase);
        Assert.Equal(ClipPhase.Exit, First(query.VisitFrames(in track, 10, TimelineDirection.Reverse)).Phase);
        Assert.Equal(ClipPhase.Active, First(query.VisitFrames(in track, 12, TimelineDirection.None)).Phase);

        Assert.Equal(0f, First(query.VisitFrames(in track, 10, TimelineDirection.Forward)).Progress, 4);
        Assert.Equal(1f, First(query.VisitFrames(in track, 14, TimelineDirection.Forward)).Progress, 4);
    }

    [Fact]
    public void SingleFrameEnterOnly()
    {
        var db = BuildSingleTrack(
            32,
            TrackMode.Exclusive,
            Clip.At(10, new TestClip(7)));

        var handle = db.Resolve(TestTypes.Clip);
        var view = db.AsView();
        var query = view.Query(handle);
        var track = query.Tracks(new TimelineIndex(0))[0];

        Assert.Equal(ClipPhase.Enter, First(query.VisitFrames(in track, 10, TimelineDirection.Forward)).Phase);
        Assert.Equal(ClipPhase.Enter, First(query.VisitFrames(in track, 10, TimelineDirection.Reverse)).Phase);
        Assert.Equal(ClipPhase.None, First(query.VisitFrames(in track, 10, TimelineDirection.None)).Phase);

        TransitionRecorder forward = default;
        query.TraverseTransitions(new TimelineSpan(new TimelineIndex(0), 9, 10), ref forward);
        Assert.Equal(1, forward.ClipEnter);
        Assert.Equal(0, forward.ClipExit);

        TransitionRecorder reverse = default;
        query.TraverseTransitions(new TimelineSpan(new TimelineIndex(0), 11, 10), ref reverse);
        Assert.Equal(1, reverse.ClipEnter);
        Assert.Equal(0, reverse.ClipExit);
    }

    [Fact]
    public void CrossFadePhases()
    {
        var db = BuildCrossFade();

        var handle = db.Resolve(TestTypes.Clip);
        var view = db.AsView();
        var query = view.Query(handle);
        var track = query.Tracks(new TimelineIndex(0))[0];

        var start = ReadFrame(query.VisitFrames(in track, 3, TimelineDirection.Forward));
        Assert.Equal(2, start.Length);
        Assert.Equal(BlendPhase.Enter, start[0].BlendPhase);
        Assert.Equal(BlendPhase.Enter, start[1].BlendPhase);
        Assert.Equal(1f, start[0].Weight, 4);
        Assert.Equal(0f, start[1].Weight, 4);

        var end = ReadFrame(query.VisitFrames(in track, 4, TimelineDirection.Forward));
        Assert.Equal(BlendPhase.Exit, end[0].BlendPhase);
        Assert.Equal(BlendPhase.Exit, end[1].BlendPhase);
        Assert.Equal(0f, end[0].Weight, 4);
        Assert.Equal(1f, end[1].Weight, 4);

        var reverseStart = ReadFrame(query.VisitFrames(in track, 4, TimelineDirection.Reverse));
        Assert.Equal(BlendPhase.Enter, reverseStart[0].BlendPhase);
        Assert.Equal(BlendPhase.Enter, reverseStart[1].BlendPhase);
    }

    [Fact]
    public void ForwardAndReverseTraversal()
    {
        var db = BuildSingleTrack(
            32,
            TrackMode.Exclusive,
            Clip.Range(10, 15, new TestClip(3)));

        var handle = db.Resolve(TestTypes.Clip);
        var view = db.AsView();
        var query = view.Query(handle);

        TransitionRecorder forward = default;
        query.TraverseTransitions(new TimelineSpan(new TimelineIndex(0), 9, 14), ref forward);
        Assert.Equal(1, forward.ClipEnter);
        Assert.Equal(1, forward.ClipExit);

        TransitionRecorder reverse = default;
        query.TraverseTransitions(new TimelineSpan(new TimelineIndex(0), 15, 10), ref reverse);
        Assert.Equal(1, reverse.ClipEnter);
        Assert.Equal(1, reverse.ClipExit);
    }

    [Fact]
    public void LoopTraversal()
    {
        DatabaseBuilder builder = new();
        var timeline = builder.AddTimeline(new TimelineKey(3), 10, TimelineFlags.Loop);
        TestClip pulse = new(9);

        builder.AddTrack(
            timeline,
            TestTypes.Clip,
            new BindingId(0),
            TrackMode.Exclusive,
            [Clip.At(0, in pulse)]);

        var db = builder.Build();
        var handle = db.Resolve(TestTypes.Clip);
        var view = db.AsView();
        var query = view.Query(handle);

        TransitionRecorder recorder = default;
        query.TraverseTransitions(new TimelineSpan(timeline, 9, 10), ref recorder);
        Assert.Equal(1, recorder.ClipEnter);
        Assert.Equal(9, recorder.Value);
    }

    [Fact]
    public void LoopTraversalGlobalTicksAreAbsolute()
    {
        DatabaseBuilder builder = new();
        var timeline = builder.AddTimeline(new TimelineKey(5), 10, TimelineFlags.Loop);
        TestClip value = new(4);

        builder.AddTrack(
            timeline,
            TestTypes.Clip,
            new BindingId(0),
            TrackMode.Exclusive,
            [Clip.Range(2, 6, in value)]);

        var db = builder.Build();
        var handle = db.Resolve(TestTypes.Clip);
        var view = db.AsView();
        var query = view.Query(handle);

        OccurrenceRecorder single = default;
        query.TraverseTransitions(new TimelineSpan(timeline, 1, 3), ref single);
        Assert.Equal(1, single.Count);
        Assert.Equal(2, single.FirstGlobalTick);
        Assert.Equal(ClipPhase.Enter, single.FirstPhase);

        OccurrenceRecorder crossed = default;
        query.TraverseTransitions(new TimelineSpan(timeline, 9, 12), ref crossed);
        Assert.Equal(1, crossed.Count);
        Assert.Equal(12, crossed.FirstGlobalTick);
        Assert.Equal(ClipPhase.Enter, crossed.FirstPhase);

        OccurrenceRecorder rewind = default;
        query.TraverseTransitions(new TimelineSpan(timeline, 3, -2), ref rewind);
        Assert.Equal(1, rewind.Count);
        Assert.Equal(2, rewind.FirstGlobalTick);
        Assert.Equal(ClipPhase.Exit, rewind.FirstPhase);
    }

    [Fact]
    public void TraverseIsTotalForDegenerateInputs()
    {
        DatabaseBuilder builder = new();
        var timeline = builder.AddTimeline(new TimelineKey(6), 10);
        TestClip value = new(2);

        builder.AddTrack(
            timeline,
            TestTypes.Clip,
            new BindingId(0),
            TrackMode.Exclusive,
            [Clip.Range(2, 6, in value)]);

        var db = builder.Build();
        var handle = db.Resolve(TestTypes.Clip);
        var view = db.AsView();
        var query = view.Query(handle);

        OccurrenceRecorder sameTick = default;
        Assert.Equal(0, query.TraverseTransitions(new TimelineSpan(timeline, 5, 5), ref sameTick));
        Assert.Equal(0, sameTick.Count);

        OccurrenceRecorder unknownTimeline = default;
        Assert.Equal(
            0,
            query.TraverseTransitions(new TimelineSpan(new TimelineIndex(99), 0, 5), ref unknownTimeline));
        Assert.Equal(0, unknownTimeline.Count);
    }

    [Fact]
    public void BakeIsDeterministicAndBlobRoundTrips()
    {
        var first = BuildCrossFade().ToArray();
        var second = BuildCrossFade().ToArray();
        Assert.True(first.AsSpan().SequenceEqual(second));

        var loaded = TimelineDatabase.Load(first);
        Assert.True(loaded.ToArray().AsSpan().SequenceEqual(first));

        first[^1] ^= 0xFF;
        Assert.True(loaded.ToArray().AsSpan().SequenceEqual(second));
        Assert.Throws<InvalidDataException>(() => TimelineDatabase.Load(first));
    }

    [Fact]
    public void LoadedViewSectionsMatchBuiltDatabase()
    {
        var built = BuildCrossFade();
        var loaded = TimelineDatabase.Load(built.ToArray());

        var a = built.AsView();
        var b = loaded.AsView();

        Assert.True(a.Timelines.SequenceEqual(b.Timelines));
        Assert.True(a.TimelineLookup.SequenceEqual(b.TimelineLookup));
        Assert.True(a.Tracks.SequenceEqual(b.Tracks));
        Assert.True(a.TrackTemplate.SequenceEqual(b.TrackTemplate));
        Assert.True(a.Clips.SequenceEqual(b.Clips));
        Assert.True(a.Types.SequenceEqual(b.Types));
        Assert.True(a.Directory.SequenceEqual(b.Directory));
        Assert.True(a.Arena.SequenceEqual(b.Arena));
    }

    [Fact]
    public void HotVisitAllocatesZero()
    {
        var db = BuildSingleTrack(
            32,
            TrackMode.Exclusive,
            Clip.Range(10, 15, new TestClip(3)));

        var handle = db.Resolve(TestTypes.Clip);
        var view = db.AsView();
        var query = view.Query(handle);
        Span<TimelineCursor> cursors = stackalloc TimelineCursor[1];
        cursors[0] = new TimelineCursor(new TimelineIndex(0), 12, TimelineDirection.Forward);
        FrameSink sink = default;

        for (var i = 0; i < 1024; i++) query.VisitFrames(cursors, ref sink);

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 100_000; i++) query.VisitFrames(cursors, ref sink);

        var after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void SampleExclusiveSingleUnitWeight()
    {
        var db = BuildSingleTrack(
            32,
            TrackMode.Exclusive,
            Clip.Range(10, 15, new TestClip(1)));

        var handle = db.Resolve(TestTypes.Clip);
        var view = db.AsView();
        var query = view.Query(handle);
        var track = query.Tracks(new TimelineIndex(0))[0];

        var samples = query.Sample(in track, 12);

        Assert.True(samples.MoveNext());
        Assert.Equal(1f, samples.Current.Weight, 4);
        Assert.Equal(1, query.Data(samples.Current.DataOffset).Value);
        Assert.False(samples.MoveNext());

        var none = query.Sample(in track, 5);
        Assert.False(none.MoveNext());
    }

    [Fact]
    public void SampleCrossFadeWeightsSumToOne()
    {
        var db = BuildCrossFade();

        var handle = db.Resolve(TestTypes.Clip);
        var view = db.AsView();
        var query = view.Query(handle);
        var track = query.Tracks(new TimelineIndex(0))[0];

        var blend = query.Sample(in track, 4);

        Assert.True(blend.MoveNext());
        var first = blend.Current;
        Assert.True(blend.MoveNext());
        var second = blend.Current;
        Assert.False(blend.MoveNext());
        Assert.Equal(1f, first.Weight + second.Weight, 4);
        Assert.Equal(1, query.Data(first.DataOffset).Value);
        Assert.Equal(2, query.Data(second.DataOffset).Value);

        var tail = query.Sample(in track, 6);

        Assert.True(tail.MoveNext());
        Assert.Equal(1f, tail.Current.Weight, 4);
        Assert.Equal(2, query.Data(tail.Current.DataOffset).Value);
        Assert.False(tail.MoveNext());
    }

    [Fact]
    public void SampleFusedMatchesVisit()
    {
        var db = BuildCrossFade();

        var handle = db.Resolve(TestTypes.Clip);
        var view = db.AsView();
        var query = view.Query(handle);
        var track = query.Tracks(new TimelineIndex(0))[0];

        SumVisitor sumVisitor = default;
        query.Sample(in track, 4, ref sumVisitor);
        Assert.Equal(2.0f, sumVisitor.Sum, 4);

        Span<TimelineCursor> cursors = stackalloc TimelineCursor[1];
        cursors[0] = new TimelineCursor(new TimelineIndex(0), 4, TimelineDirection.Forward);
        FrameSink sink = default;
        query.VisitFrames(cursors, ref sink);
        Assert.Equal(2.0f, sink.Sum, 4);

        Assert.Equal(sumVisitor.Sum, sink.Sum, 4);
    }

    [Fact]
    public void TrackTemplateExposesMode()
    {
        var exclusive = BuildSingleTrack(
            32,
            TrackMode.Exclusive,
            Clip.Range(10, 15, new TestClip(1)));
        var crossFade = BuildCrossFade();

        var exclusiveHandle = exclusive.Resolve(TestTypes.Clip);
        var exclusiveView = exclusive.AsView();
        var exclusiveQuery = exclusiveView.Query(exclusiveHandle);
        var exclusiveTrack = exclusiveQuery.Tracks(new TimelineIndex(0))[0];
        Assert.Equal(TrackMode.Exclusive, exclusiveQuery.TrackTemplate(in exclusiveTrack).Mode);

        var crossFadeHandle = crossFade.Resolve(TestTypes.Clip);
        var crossFadeView = crossFade.AsView();
        var crossFadeQuery = crossFadeView.Query(crossFadeHandle);
        var crossFadeTrack = crossFadeQuery.Tracks(new TimelineIndex(0))[0];
        Assert.Equal(TrackMode.CrossFade, crossFadeQuery.TrackTemplate(in crossFadeTrack).Mode);
    }

    [Fact]
    public void TrackRefExposesMode()
    {
        var exclusive = BuildSingleTrack(
            32,
            TrackMode.Exclusive,
            Clip.Range(10, 15, new TestClip(1)));
        var crossFade = BuildCrossFade();

        var exclusiveHandle = exclusive.Resolve(TestTypes.Clip);
        var exclusiveView = exclusive.AsView();
        var exclusiveQuery = exclusiveView.Query(exclusiveHandle);
        var exclusiveTrack = exclusiveQuery.Tracks(new TimelineIndex(0))[0];
        var exclusiveRef = exclusiveQuery.Track(in exclusiveTrack);
        Assert.Equal(TrackMode.Exclusive, exclusiveRef.Mode);
        Assert.Equal(0, exclusiveRef.Binding);

        var crossFadeHandle = crossFade.Resolve(TestTypes.Clip);
        var crossFadeView = crossFade.AsView();
        var crossFadeQuery = crossFadeView.Query(crossFadeHandle);
        var crossFadeTrack = crossFadeQuery.Tracks(new TimelineIndex(0))[0];
        var crossFadeRef = crossFadeQuery.Track(in crossFadeTrack);
        Assert.Equal(TrackMode.CrossFade, crossFadeRef.Mode);
        Assert.Equal(0, crossFadeRef.Binding);
    }

    [Fact]
    public void MaxWidthAuthoringRoundTrips()
    {
        DatabaseBuilder builder = new();
        var timeline = builder.AddTimeline(new TimelineKey(0xA11CE), Format.MaxTimelineDuration);
        builder.AddTrack(
            timeline,
            TestTypes.Clip,
            new BindingId(Format.MaxBinding),
            TrackMode.Exclusive,
            [Clip.Range(0, Format.MaxTimelineDuration, new TestClip(7))]);

        var db = builder.Build();
        var loaded = TimelineDatabase.Load(db.ToArray());

        var view = loaded.AsView();
        Assert.Equal(Format.MaxTimelineDuration, view.Timelines[0].Duration);
        Assert.True(view.TryResolve(new TimelineKey(0xA11CE), out var resolved));
        Assert.Equal(timeline, resolved);

        var query = view.Query(loaded.Resolve(TestTypes.Clip));
        var track = query.Tracks(timeline)[0];
        Assert.Equal(Format.MaxBinding, track.Binding);

        var samples = query.Sample(in track, Format.MaxTimelineDuration - 1);
        Assert.True(samples.MoveNext());
        Assert.Equal(7, query.Data(samples.Current.DataOffset).Value);
        Assert.Equal(1f, samples.Current.Weight);
    }

    [Fact]
    public void BlobV2RejectsOversizedAuthoring()
    {
        DatabaseBuilder builder = new();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.AddTimeline(new TimelineKey(0x1), Format.MaxTimelineDuration + 1));

        var timeline = builder.AddTimeline(new TimelineKey(0x2), 64);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.AddTrack(
                timeline,
                TestTypes.Clip,
                new BindingId(Format.MaxBinding + 1),
                TrackMode.Exclusive,
                [Clip.Range(0, 64, new TestClip(1))]));
    }

    private static TimelineDatabase BuildSingleTrack(
        int duration,
        TrackMode mode,
        ClipDefinition<TestClip> clip)
    {
        DatabaseBuilder builder = new();
        var timeline = builder.AddTimeline(new TimelineKey(1), duration);
        builder.AddTrack(
            timeline,
            TestTypes.Clip,
            new BindingId(0),
            mode,
            [clip]);
        return builder.Build();
    }

    private static TimelineDatabase BuildCrossFade()
    {
        DatabaseBuilder builder = new();
        var timeline = builder.AddTimeline(new TimelineKey(2), 20);
        TestClip a = new(1);
        TestClip b = new(2);

        builder.AddTrack(
            timeline,
            TestTypes.Clip,
            new BindingId(0),
            TrackMode.CrossFade,
            [
                Clip.Range(0, 5, in a),
                Clip.Range(3, 8, in b)
            ]);

        return builder.Build();
    }

    private static ClipFrame First(ClipFrameEnumerator frame)
    {
        Assert.True(frame.MoveNext(), "Expected one clip frame.");

        return frame.Current;
    }

    private static ClipFrame[] ReadFrame(ClipFrameEnumerator frame)
    {
        List<ClipFrame> result = [];

        while (frame.MoveNext()) result.Add(frame.Current);

        return [.. result];
    }

    private struct FrameSink : IClipFrameVisitor<TestClip>
    {
        public float Sum;

        public void Visit(in TrackInstance track, in ClipFrame frame, in TestClip clip)
        {
            Sum += clip.Value * frame.Weight;
        }
    }

    private struct SumVisitor : IClipSampleVisitor<TestClip>
    {
        public float Sum;

        public void Sample(in TrackInstance track, in TestClip clip, float weight)
        {
            Sum += clip.Value * weight;
        }
    }
}