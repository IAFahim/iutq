using System.Runtime.InteropServices;
using Iutq.Core;
using Xunit;

namespace Iutq.Tests;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TestClip
{
    public int Value;

    public TestClip(int value) => Value = value;
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

    public void Clip(in TrackInstance track, in ClipTransition transition, in TestClip clip)
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

    public void Blend(
        in TrackInstance track,
        in BlendTransition transition,
        in TestClip clipA,
        in TestClip clipB)
    {
        if (transition.Phase == BlendPhase.Enter)
        {
            BlendEnter++;
        }
        else if (transition.Phase == BlendPhase.Exit)
        {
            BlendExit++;
        }
    }
}

public struct OccurrenceRecorder : IClipTransitionVisitor<TestClip>
{
    public long FirstOccurrenceTick;
    public long SecondOccurrenceTick;
    public ClipPhase FirstPhase;
    public int Count;

    public void Clip(in TrackInstance track, in ClipTransition transition, in TestClip clip)
    {
        if (Count == 0)
        {
            FirstOccurrenceTick = transition.OccurrenceTick;
            FirstPhase = transition.Phase;
        }
        else if (Count == 1)
        {
            SecondOccurrenceTick = transition.OccurrenceTick;
        }

        Count++;
    }

    public void Blend(
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
        TimelineDatabase db = BuildSingleTrack(
            32,
            TrackMode.Exclusive,
            Clip.Range(10, 15, new TestClip(1)));

        ClipHandle<TestClip> handle = db.Resolve(TestTypes.Clip);
        DatabaseView view = db.AsView();
        ClipQuery<TestClip> query = view.Query(handle);
        TrackInstance track = query.Tracks(new TimelineId(0))[0];

        Assert.Equal(ClipPhase.Enter, First(query.Frame(in track, 10, TimelineDirection.Forward)).Phase);
        Assert.Equal(ClipPhase.Stay, First(query.Frame(in track, 11, TimelineDirection.Forward)).Phase);
        Assert.Equal(ClipPhase.Stay, First(query.Frame(in track, 13, TimelineDirection.Forward)).Phase);
        Assert.Equal(ClipPhase.Exit, First(query.Frame(in track, 14, TimelineDirection.Forward)).Phase);

        Assert.Equal(ClipPhase.Enter, First(query.Frame(in track, 14, TimelineDirection.Reverse)).Phase);
        Assert.Equal(ClipPhase.Stay, First(query.Frame(in track, 13, TimelineDirection.Reverse)).Phase);
        Assert.Equal(ClipPhase.Stay, First(query.Frame(in track, 11, TimelineDirection.Reverse)).Phase);
        Assert.Equal(ClipPhase.Exit, First(query.Frame(in track, 10, TimelineDirection.Reverse)).Phase);
        Assert.Equal(ClipPhase.Stay, First(query.Frame(in track, 12, TimelineDirection.None)).Phase);

        Assert.Equal(0f, First(query.Frame(in track, 10, TimelineDirection.Forward)).Progress, precision: 4);
        Assert.Equal(1f, First(query.Frame(in track, 14, TimelineDirection.Forward)).Progress, precision: 4);
    }

    [Fact]
    public void SingleFrameEnterOnly()
    {
        TimelineDatabase db = BuildSingleTrack(
            32,
            TrackMode.Exclusive,
            Clip.At(10, new TestClip(7)));

        ClipHandle<TestClip> handle = db.Resolve(TestTypes.Clip);
        DatabaseView view = db.AsView();
        ClipQuery<TestClip> query = view.Query(handle);
        TrackInstance track = query.Tracks(new TimelineId(0))[0];

        Assert.Equal(ClipPhase.Enter, First(query.Frame(in track, 10, TimelineDirection.Forward)).Phase);
        Assert.Equal(ClipPhase.Enter, First(query.Frame(in track, 10, TimelineDirection.Reverse)).Phase);
        Assert.Equal(ClipPhase.None, First(query.Frame(in track, 10, TimelineDirection.None)).Phase);

        TransitionRecorder forward = default;
        query.TraverseTransitions(new TimelineSpan(new TimelineId(0), 9, 10), ref forward);
        Assert.Equal(1, forward.ClipEnter);
        Assert.Equal(0, forward.ClipExit);

        TransitionRecorder reverse = default;
        query.TraverseTransitions(new TimelineSpan(new TimelineId(0), 11, 10), ref reverse);
        Assert.Equal(1, reverse.ClipEnter);
        Assert.Equal(0, reverse.ClipExit);
    }

    [Fact]
    public void CrossFadePhases()
    {
        TimelineDatabase db = BuildCrossFade();

        ClipHandle<TestClip> handle = db.Resolve(TestTypes.Clip);
        DatabaseView view = db.AsView();
        ClipQuery<TestClip> query = view.Query(handle);
        TrackInstance track = query.Tracks(new TimelineId(0))[0];

        ClipHit[] start = ReadFrame(query.Frame(in track, 3, TimelineDirection.Forward));
        Assert.Equal(2, start.Length);
        Assert.Equal(BlendPhase.Enter, start[0].BlendPhase);
        Assert.Equal(BlendPhase.Enter, start[1].BlendPhase);
        Assert.Equal(1f, start[0].Weight, precision: 4);
        Assert.Equal(0f, start[1].Weight, precision: 4);

        ClipHit[] end = ReadFrame(query.Frame(in track, 4, TimelineDirection.Forward));
        Assert.Equal(BlendPhase.Exit, end[0].BlendPhase);
        Assert.Equal(BlendPhase.Exit, end[1].BlendPhase);
        Assert.Equal(0f, end[0].Weight, precision: 4);
        Assert.Equal(1f, end[1].Weight, precision: 4);

        ClipHit[] reverseStart = ReadFrame(query.Frame(in track, 4, TimelineDirection.Reverse));
        Assert.Equal(BlendPhase.Enter, reverseStart[0].BlendPhase);
        Assert.Equal(BlendPhase.Enter, reverseStart[1].BlendPhase);
    }

    [Fact]
    public void ForwardAndReverseTraversal()
    {
        TimelineDatabase db = BuildSingleTrack(
            32,
            TrackMode.Exclusive,
            Clip.Range(10, 15, new TestClip(3)));

        ClipHandle<TestClip> handle = db.Resolve(TestTypes.Clip);
        DatabaseView view = db.AsView();
        ClipQuery<TestClip> query = view.Query(handle);

        TransitionRecorder forward = default;
        query.TraverseTransitions(new TimelineSpan(new TimelineId(0), 9, 14), ref forward);
        Assert.Equal(1, forward.ClipEnter);
        Assert.Equal(1, forward.ClipExit);

        TransitionRecorder reverse = default;
        query.TraverseTransitions(new TimelineSpan(new TimelineId(0), 15, 10), ref reverse);
        Assert.Equal(1, reverse.ClipEnter);
        Assert.Equal(1, reverse.ClipExit);
    }

    [Fact]
    public void LoopTraversal()
    {
        DatabaseBuilder builder = new();
        TimelineId timeline = builder.AddTimeline(new TimelineKey(3), 10, TimelineFlags.Loop);
        TestClip pulse = new(9);

        builder.AddTrack(
            timeline,
            TestTypes.Clip,
            new BindingId(0),
            TrackMode.Exclusive,
            [Clip.At(0, in pulse)]);

        TimelineDatabase db = builder.Build();
        ClipHandle<TestClip> handle = db.Resolve(TestTypes.Clip);
        DatabaseView view = db.AsView();
        ClipQuery<TestClip> query = view.Query(handle);

        TransitionRecorder recorder = default;
        query.TraverseTransitions(new TimelineSpan(timeline, 9, 10), ref recorder);
        Assert.Equal(1, recorder.ClipEnter);
        Assert.Equal(9, recorder.Value);
    }

    [Fact]
    public void LoopTraversalOccurrenceTicksAreAbsolute()
    {
        DatabaseBuilder builder = new();
        TimelineId timeline = builder.AddTimeline(new TimelineKey(5), 10, TimelineFlags.Loop);
        TestClip value = new(4);

        builder.AddTrack(
            timeline,
            TestTypes.Clip,
            new BindingId(0),
            TrackMode.Exclusive,
            [Clip.Range(2, 6, in value)]);

        TimelineDatabase db = builder.Build();
        ClipHandle<TestClip> handle = db.Resolve(TestTypes.Clip);
        DatabaseView view = db.AsView();
        ClipQuery<TestClip> query = view.Query(handle);

        OccurrenceRecorder single = default;
        query.TraverseTransitions(new TimelineSpan(timeline, 1, 3), ref single);
        Assert.Equal(1, single.Count);
        Assert.Equal(2, single.FirstOccurrenceTick);
        Assert.Equal(ClipPhase.Enter, single.FirstPhase);

        OccurrenceRecorder crossed = default;
        query.TraverseTransitions(new TimelineSpan(timeline, 9, 12), ref crossed);
        Assert.Equal(1, crossed.Count);
        Assert.Equal(12, crossed.FirstOccurrenceTick);
        Assert.Equal(ClipPhase.Enter, crossed.FirstPhase);

        OccurrenceRecorder rewind = default;
        query.TraverseTransitions(new TimelineSpan(timeline, 3, -2), ref rewind);
        Assert.Equal(1, rewind.Count);
        Assert.Equal(2, rewind.FirstOccurrenceTick);
        Assert.Equal(ClipPhase.Exit, rewind.FirstPhase);
    }

    [Fact]
    public void TraverseIsTotalForDegenerateInputs()
    {
        DatabaseBuilder builder = new();
        TimelineId timeline = builder.AddTimeline(new TimelineKey(6), 10);
        TestClip value = new(2);

        builder.AddTrack(
            timeline,
            TestTypes.Clip,
            new BindingId(0),
            TrackMode.Exclusive,
            [Clip.Range(2, 6, in value)]);

        TimelineDatabase db = builder.Build();
        ClipHandle<TestClip> handle = db.Resolve(TestTypes.Clip);
        DatabaseView view = db.AsView();
        ClipQuery<TestClip> query = view.Query(handle);

        OccurrenceRecorder sameTick = default;
        Assert.Equal(0, query.TraverseTransitions(new TimelineSpan(timeline, 5, 5), ref sameTick));
        Assert.Equal(0, sameTick.Count);

        OccurrenceRecorder unknownTimeline = default;
        Assert.Equal(
            0,
            query.TraverseTransitions(new TimelineSpan(new TimelineId(99), 0, 5), ref unknownTimeline));
        Assert.Equal(0, unknownTimeline.Count);
    }

    [Fact]
    public void BakeIsDeterministicAndBlobRoundTrips()
    {
        byte[] first = BuildCrossFade().ToArray();
        byte[] second = BuildCrossFade().ToArray();
        Assert.True(first.AsSpan().SequenceEqual(second));

        TimelineDatabase loaded = TimelineDatabase.Load(first);
        Assert.True(loaded.ToArray().AsSpan().SequenceEqual(first));

        first[^1] ^= 0xFF;
        Assert.True(loaded.ToArray().AsSpan().SequenceEqual(second));
        Assert.Throws<InvalidDataException>(() => TimelineDatabase.Load(first));
    }

    [Fact]
    public void LoadedViewSectionsMatchBuiltDatabase()
    {
        TimelineDatabase built = BuildCrossFade();
        TimelineDatabase loaded = TimelineDatabase.Load(built.ToArray());

        DatabaseView a = built.AsView();
        DatabaseView b = loaded.AsView();

        Assert.True(a.Timelines.SequenceEqual(b.Timelines));
        Assert.True(a.TimelineLookup.SequenceEqual(b.TimelineLookup));
        Assert.True(a.Tracks.SequenceEqual(b.Tracks));
        Assert.True(a.TrackData.SequenceEqual(b.TrackData));
        Assert.True(a.Clips.SequenceEqual(b.Clips));
        Assert.True(a.Types.SequenceEqual(b.Types));
        Assert.True(a.Directory.SequenceEqual(b.Directory));
        Assert.True(a.Arena.SequenceEqual(b.Arena));
    }

    [Fact]
    public void HotVisitAllocatesZero()
    {
        TimelineDatabase db = BuildSingleTrack(
            32,
            TrackMode.Exclusive,
            Clip.Range(10, 15, new TestClip(3)));

        ClipHandle<TestClip> handle = db.Resolve(TestTypes.Clip);
        DatabaseView view = db.AsView();
        ClipQuery<TestClip> query = view.Query(handle);
        Span<TimelineCursor> cursors = stackalloc TimelineCursor[1];
        cursors[0] = new TimelineCursor(new TimelineId(0), 12, TimelineDirection.Forward);
        FrameSink sink = default;

        for (int i = 0; i < 1024; i++)
        {
            query.Visit(cursors, ref sink);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 100_000; i++)
        {
            query.Visit(cursors, ref sink);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void SampleExclusiveSingleUnitWeight()
    {
        TimelineDatabase db = BuildSingleTrack(
            32,
            TrackMode.Exclusive,
            Clip.Range(10, 15, new TestClip(1)));

        ClipHandle<TestClip> handle = db.Resolve(TestTypes.Clip);
        DatabaseView view = db.AsView();
        ClipQuery<TestClip> query = view.Query(handle);
        TrackInstance track = query.Tracks(new TimelineId(0))[0];

        ClipSampleEnumerator samples = query.Sample(in track, 12);

        Assert.True(samples.MoveNext());
        Assert.Equal(1f, samples.Current.Weight, precision: 4);
        Assert.Equal(1, query.Data(samples.Current.DataOffset).Value);
        Assert.False(samples.MoveNext());

        ClipSampleEnumerator none = query.Sample(in track, 5);
        Assert.False(none.MoveNext());
    }

    [Fact]
    public void SampleCrossFadeWeightsSumToOne()
    {
        TimelineDatabase db = BuildCrossFade();

        ClipHandle<TestClip> handle = db.Resolve(TestTypes.Clip);
        DatabaseView view = db.AsView();
        ClipQuery<TestClip> query = view.Query(handle);
        TrackInstance track = query.Tracks(new TimelineId(0))[0];

        ClipSampleEnumerator blend = query.Sample(in track, 4);

        Assert.True(blend.MoveNext());
        ClipSample first = blend.Current;
        Assert.True(blend.MoveNext());
        ClipSample second = blend.Current;
        Assert.False(blend.MoveNext());
        Assert.Equal(1f, first.Weight + second.Weight, precision: 4);
        Assert.Equal(1, query.Data(first.DataOffset).Value);
        Assert.Equal(2, query.Data(second.DataOffset).Value);

        ClipSampleEnumerator tail = query.Sample(in track, 6);

        Assert.True(tail.MoveNext());
        Assert.Equal(1f, tail.Current.Weight, precision: 4);
        Assert.Equal(2, query.Data(tail.Current.DataOffset).Value);
        Assert.False(tail.MoveNext());
    }

    [Fact]
    public void SampleFusedMatchesVisit()
    {
        TimelineDatabase db = BuildCrossFade();

        ClipHandle<TestClip> handle = db.Resolve(TestTypes.Clip);
        DatabaseView view = db.AsView();
        ClipQuery<TestClip> query = view.Query(handle);
        TrackInstance track = query.Tracks(new TimelineId(0))[0];

        SumVisitor sumVisitor = default;
        query.Sample(in track, 4, ref sumVisitor);
        Assert.Equal(2.0f, sumVisitor.Sum, precision: 4);

        Span<TimelineCursor> cursors = stackalloc TimelineCursor[1];
        cursors[0] = new TimelineCursor(new TimelineId(0), 4, TimelineDirection.Forward);
        FrameSink sink = default;
        query.Visit(cursors, ref sink);
        Assert.Equal(2.0f, sink.Sum, precision: 4);

        Assert.Equal(sumVisitor.Sum, sink.Sum, precision: 4);
    }

    [Fact]
    public void TrackDataExposesMode()
    {
        TimelineDatabase exclusive = BuildSingleTrack(
            32,
            TrackMode.Exclusive,
            Clip.Range(10, 15, new TestClip(1)));
        TimelineDatabase crossFade = BuildCrossFade();

        ClipHandle<TestClip> exclusiveHandle = exclusive.Resolve(TestTypes.Clip);
        DatabaseView exclusiveView = exclusive.AsView();
        ClipQuery<TestClip> exclusiveQuery = exclusiveView.Query(exclusiveHandle);
        TrackInstance exclusiveTrack = exclusiveQuery.Tracks(new TimelineId(0))[0];
        Assert.Equal(TrackMode.Exclusive, exclusiveQuery.TrackData(in exclusiveTrack).Mode);

        ClipHandle<TestClip> crossFadeHandle = crossFade.Resolve(TestTypes.Clip);
        DatabaseView crossFadeView = crossFade.AsView();
        ClipQuery<TestClip> crossFadeQuery = crossFadeView.Query(crossFadeHandle);
        TrackInstance crossFadeTrack = crossFadeQuery.Tracks(new TimelineId(0))[0];
        Assert.Equal(TrackMode.CrossFade, crossFadeQuery.TrackData(in crossFadeTrack).Mode);
    }

    [Fact]
    public void TrackRefExposesMode()
    {
        TimelineDatabase exclusive = BuildSingleTrack(
            32,
            TrackMode.Exclusive,
            Clip.Range(10, 15, new TestClip(1)));
        TimelineDatabase crossFade = BuildCrossFade();

        ClipHandle<TestClip> exclusiveHandle = exclusive.Resolve(TestTypes.Clip);
        DatabaseView exclusiveView = exclusive.AsView();
        ClipQuery<TestClip> exclusiveQuery = exclusiveView.Query(exclusiveHandle);
        TrackInstance exclusiveTrack = exclusiveQuery.Tracks(new TimelineId(0))[0];
        TrackRef exclusiveRef = exclusiveQuery.Track(in exclusiveTrack);
        Assert.Equal(TrackMode.Exclusive, exclusiveRef.Mode);
        Assert.Equal(0, exclusiveRef.Binding);

        ClipHandle<TestClip> crossFadeHandle = crossFade.Resolve(TestTypes.Clip);
        DatabaseView crossFadeView = crossFade.AsView();
        ClipQuery<TestClip> crossFadeQuery = crossFadeView.Query(crossFadeHandle);
        TrackInstance crossFadeTrack = crossFadeQuery.Tracks(new TimelineId(0))[0];
        TrackRef crossFadeRef = crossFadeQuery.Track(in crossFadeTrack);
        Assert.Equal(TrackMode.CrossFade, crossFadeRef.Mode);
        Assert.Equal(0, crossFadeRef.Binding);
    }

    private struct FrameSink : IClipFrameVisitor<TestClip>
    {
        public float Sum;

        public void Visit(in TrackInstance track, in ClipHit hit, in TestClip clip)
        {
            Sum += clip.Value * hit.Weight;
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

    private static TimelineDatabase BuildSingleTrack(
        int duration,
        TrackMode mode,
        ClipDefinition<TestClip> clip)
    {
        DatabaseBuilder builder = new();
        TimelineId timeline = builder.AddTimeline(new TimelineKey(1), duration);
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
        TimelineId timeline = builder.AddTimeline(new TimelineKey(2), 20);
        TestClip a = new(1);
        TestClip b = new(2);

        builder.AddTrack(
            timeline,
            TestTypes.Clip,
            new BindingId(0),
            TrackMode.CrossFade,
            [
                Clip.Range(0, 5, in a),
                Clip.Range(3, 8, in b),
            ]);

        return builder.Build();
    }

    private static ClipHit First(ClipFrameEnumerator frame)
    {
        Assert.True(frame.MoveNext(), "Expected one clip hit.");

        return frame.Current;
    }

    private static ClipHit[] ReadFrame(ClipFrameEnumerator frame)
    {
        List<ClipHit> result = [];

        while (frame.MoveNext())
        {
            result.Add(frame.Current);
        }

        return [.. result];
    }
}
