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

    public void Clip(in ClipTransition transition, in TestClip clip)
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

        TimelineDatabase db = builder.Build();
        ClipHandle<TestClip> handle = db.Resolve(TestTypes.Clip);
        DatabaseView view = db.AsView();
        ClipQuery<TestClip> query = view.Query(handle);
        TrackInstance track = query.Tracks(timeline)[0];

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

    private struct FrameSink : IClipFrameVisitor<TestClip>
    {
        public int Sum;

        public void Visit(in ClipHit hit, in TestClip clip)
        {
            Sum += clip.Value;
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
