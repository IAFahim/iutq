using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Iutq.Core.Baking;
using Iutq.Core.Primitives;
using Iutq.Core.Querying;
using Iutq.Core.Storage;
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

    [Fact]
    public void FromNameKeysAreStableAndDistinct()
    {
        var a = TimelineKey.FromName("combat.light_attack");
        var b = TimelineKey.FromName("combat.light_attack");
        var c = TimelineKey.FromName("combat.heavy_attack");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(0UL, a.Value);

        var type = ClipType<TestClip>.FromName("game.test_clip");
        Assert.Equal(ClipType<TestClip>.FromName("game.test_clip").Key, type.Key);
        Assert.NotEqual(ClipType<TestClip>.FromName("game.other_clip").Key, type.Key);
        Assert.NotEqual(0UL, type.Key);

        DatabaseBuilder builder = new();
        var timeline = builder.AddTimeline(TimelineKey.FromName("combat.light_attack"), 20);
        builder.AddTrack(
            timeline,
            ClipType<TestClip>.FromName("game.test_clip"),
            new BindingId(0),
            TrackMode.Exclusive,
            [Clip.Range(0, 10, new TestClip(5))]);

        var db = builder.Build();
        var view = db.AsView();
        Assert.True(view.TryResolve(TimelineKey.FromName("combat.light_attack"), out var resolved));
        Assert.Equal(timeline, resolved);

        var query = view.Query(ClipType<TestClip>.FromName("game.test_clip"));
        var track = query.Tracks(resolved)[0];
        var samples = query.Sample(in track, 3);
        Assert.True(samples.MoveNext());
        Assert.Equal(5, query.Data(samples.Current.DataOffset).Value);
    }

    [Fact]
    public void QueryByTypeOverloadResolvesAndThrows()
    {
        var db = BuildCrossFade();
        var view = db.AsView();

        var query = view.Query(TestTypes.Clip);
        var track = query.Tracks(new TimelineIndex(0))[0];
        var samples = query.Sample(in track, 1);
        Assert.True(samples.MoveNext());
        Assert.Equal(1f, samples.Current.Weight, 4);

        Assert.Throws<KeyNotFoundException>(
            () => db.AsView().Query(ClipType<TestClip>.FromName("game.missing")));
    }

    [Fact]
    public void LambdaOverloadsMatchVisitorForms()
    {
        var db = BuildCrossFade();
        var view = db.AsView();
        var query = view.Query(TestTypes.Clip);
        var track = query.Tracks(new TimelineIndex(0))[0];

        SumVisitor visitor = default;
        query.Sample(in track, 4, ref visitor);

        float lambdaSum = 0f;
        query.Sample(
            in track,
            4,
            ref lambdaSum,
            static (ref float sum, in TrackInstance t, in TestClip clip, float weight) => sum += clip.Value * weight);
        Assert.Equal(visitor.Sum, lambdaSum, 5);

        Span<TimelineCursor> cursors = stackalloc TimelineCursor[1];
        cursors[0] = new TimelineCursor(new TimelineIndex(0), 4, TimelineDirection.Forward);

        SumVisitor spanVisitor = default;
        query.Sample(cursors, ref spanVisitor);

        float spanLambda = 0f;
        query.Sample(
            cursors,
            ref spanLambda,
            static (ref float sum, in TrackInstance t, in TestClip clip, float weight) => sum += clip.Value * weight);
        Assert.Equal(spanVisitor.Sum, spanLambda, 5);

        FrameSink frameSink = default;
        query.VisitFrames(cursors, ref frameSink);

        float frameLambda = 0f;
        query.VisitFrames(
            cursors,
            ref frameLambda,
            static (ref float sum, in TrackInstance t, in ClipFrame frame, in TestClip clip) =>
                sum += clip.Value * frame.Weight);
        Assert.Equal(frameSink.Sum, frameLambda, 5);

        TransitionRecorder recorder = default;
        query.TraverseTransitions(new TimelineSpan(new TimelineIndex(0), 2, 5), ref recorder);

        int lambdaEnter = 0;
        query.TraverseTransitions(
            new TimelineSpan(new TimelineIndex(0), 2, 5),
            ref lambdaEnter,
            static (ref int enter, in TrackInstance t, in ClipTransition transition, in TestClip clip) =>
            {
                if (transition.Phase == ClipPhase.Enter) enter++;
            },
            static (ref int enter, in TrackInstance t, in BlendTransition transition, in TestClip clipA, in TestClip clipB) =>
            {
                if (transition.Phase == BlendPhase.Enter) enter += 100;
            });

        Assert.Equal(recorder.ClipEnter, lambdaEnter % 100);
        Assert.Equal(recorder.BlendEnter * 100, lambdaEnter - lambdaEnter % 100);
    }

    [Fact]
    public void FastLookupSamplingAndFramesAreBitExact()
    {
        var (searched, fast) = BuildParityPair();

        var a = searched.AsView();
        var b = fast.AsView();
        var qa = a.Query(TestTypes.Clip);
        var qb = b.Query(TestTypes.Clip);

        for (var timeline = 0; timeline < a.Timelines.Length; timeline++)
        {
            var tracksA = qa.Tracks(new TimelineIndex(timeline));
            var tracksB = qb.Tracks(new TimelineIndex(timeline));
            Assert.Equal(tracksA.Length, tracksB.Length);

            for (var trackIndex = 0; trackIndex < tracksA.Length; trackIndex++)
            {
                ref readonly var trackA = ref tracksA[trackIndex];
                ref readonly var trackB = ref tracksB[trackIndex];

                for (var tick = 0; tick < a.Timelines[timeline].Duration; tick++)
                {
                    var samplesA = qa.Sample(in trackA, tick);
                    var samplesB = qb.Sample(in trackB, tick);

                    while (true)
                    {
                        var movedA = samplesA.MoveNext();
                        Assert.Equal(movedA, samplesB.MoveNext());

                        if (!movedA) break;

                        Assert.Equal(samplesA.Current.DataOffset, samplesB.Current.DataOffset);
                        Assert.Equal(samplesA.Current.Weight, samplesB.Current.Weight);
                    }

                    foreach (var direction in new[] { TimelineDirection.Forward, TimelineDirection.Reverse, TimelineDirection.None })
                    {
                        var framesA = qa.VisitFrames(in trackA, tick, direction);
                        var framesB = qb.VisitFrames(in trackB, tick, direction);

                        while (true)
                        {
                            var movedA = framesA.MoveNext();
                            Assert.Equal(movedA, framesB.MoveNext());

                            if (!movedA) break;

                            Assert.Equal(framesA.Current.Start, framesB.Current.Start);
                            Assert.Equal(framesA.Current.End, framesB.Current.End);
                            Assert.Equal(framesA.Current.DataOffset, framesB.Current.DataOffset);
                            Assert.Equal(framesA.Current.Weight, framesB.Current.Weight);
                            Assert.Equal(framesA.Current.BlendFactor, framesB.Current.BlendFactor);
                            Assert.Equal(framesA.Current.BlendPhase, framesB.Current.BlendPhase);
                            Assert.Equal(framesA.Current.Phase, framesB.Current.Phase);
                            Assert.Equal(framesA.Current.Progress, framesB.Current.Progress);
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void FastLookupTraversalIsBitExact()
    {
        var (searched, fast) = BuildParityPair();

        var qa = searched.AsView().Query(TestTypes.Clip);
        var qb = fast.AsFastView().Query(TestTypes.Clip);

        // (previous, current) pairs: single-tick, rewind, loop crossings, full span.
        (long Previous, long Current, TimelineIndex Timeline)[] spans =
        [
            (4, 5, new TimelineIndex(0)),
            (30, 5, new TimelineIndex(0)),
            (0, 63, new TimelineIndex(0)),
            (9, 12, new TimelineIndex(1)),
            (3, -2, new TimelineIndex(1)),
            (9, 10, new TimelineIndex(1)),
            (5, 6, new TimelineIndex(2)),
            (2200, 2600, new TimelineIndex(2))
        ];

        foreach (var (previous, current, timeline) in spans)
        {
            var span = new TimelineSpan(timeline, previous, current);

            FullTransitionRecorder ra = default;
            FullTransitionRecorder rb = default;
            var emittedA = qa.TraverseTransitions(in span, ref ra);
            var emittedB = qb.TraverseTransitions(in span, ref rb);

            Assert.Equal(emittedA, emittedB);
            Assert.Equal(ra.Count, rb.Count);
            Assert.Equal(ra.TickSum, rb.TickSum);
            Assert.Equal(ra.OffsetSum, rb.OffsetSum);
            Assert.Equal(ra.PhaseSum, rb.PhaseSum);
            Assert.Equal(ra.FactorSum, rb.FactorSum);
        }
    }

    [Fact]
    public void FastLookupBlobRoundTripsThroughLoad()
    {
        var (searched, fast) = BuildParityPair();

        var loaded = TimelineDatabase.Load(fast.ToArray());
        var qa = searched.AsView().Query(TestTypes.Clip);
        var qb = loaded.AsFastView().Query(TestTypes.Clip);

        var trackA = qa.Tracks(new TimelineIndex(0))[0];
        var trackB = qb.Tracks(new TimelineIndex(0))[0];

        var samplesA = qa.Sample(in trackA, 20);
        var samplesB = qb.Sample(in trackB, 20);

        while (samplesA.MoveNext())
        {
            Assert.True(samplesB.MoveNext());
            Assert.Equal(samplesA.Current.DataOffset, samplesB.Current.DataOffset);
            Assert.Equal(samplesA.Current.Weight, samplesB.Current.Weight);
        }

        Assert.False(samplesB.MoveNext());

        // Flag-free blobs are untouched by the extension.
        Assert.True(fast.ToArray().Length > searched.ToArray().Length);
    }

    [Fact]
    public void FastLookupBlobRejectsTampering()
    {
        DatabaseBuilder builder = new();
        var timeline = builder.AddTimeline(new TimelineKey(9), 64);
        ClipDefinition<TestClip>[] clips = new ClipDefinition<TestClip>[16];

        for (var i = 0; i < 16; i++)
        {
            var payload = new TestClip(i + 1);
            clips[i] = Clip.Range(i * 4, i * 4 + 4, in payload);
        }

        builder.AddTrack(timeline, TestTypes.Clip, new BindingId(0), TrackMode.Exclusive, clips);

        var plain = builder.Build(false).ToArray();
        var fast = builder.Build(true).ToArray();

        // Any raw byte flip breaks the payload hash first.
        var tampered = builder.Build(true).ToArray();
        tampered[^1] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => TimelineDatabase.Load(tampered));

        // Reserved header words must stay zero (offset known: the fast section
        // starts at Align8(plain length) and Reserved0 sits 10 bytes in).
        var reserved = fast.AsSpan().ToArray();
        var fastStart = (plain.Length + 7) & ~7;
        reserved[fastStart + 10] = 0xAA;
        Assert.Contains("IUTQ1006", Assert.Throws<InvalidDataException>(() => TimelineDatabase.Load(WithFixedHash(reserved))).Message);

        // A LUT entry outside the template's clip window is rejected. The u8
        // area is the section's last area: with one narrow exclusive track of
        // 64 ticks it is exactly the final 64 bytes of the blob.
        var lut = fast.AsSpan().ToArray();
        lut[^30] = 200;
        Assert.Contains("IUTQ1006", Assert.Throws<InvalidDataException>(() => TimelineDatabase.Load(WithFixedHash(lut))).Message);

        GC.KeepAlive(plain);
    }

    [Fact]
    public void FastLookupLongTimelineFallsBackToSearch()
    {
        var (searched, fast) = BuildParityPair();

        // Timeline 2 (duration 3000) exceeds every LUT strategy: both
        // databases must still agree tick for tick.
        var qa = searched.AsView().Query(TestTypes.Clip);
        var qb = fast.AsFastView().Query(TestTypes.Clip);
        var trackA = qa.Tracks(new TimelineIndex(2))[0];
        var trackB = qb.Tracks(new TimelineIndex(2))[0];

        foreach (var tick in new[] { 0, 999, 1000, 1500, 1999, 2000, 2499, 2999 })
        {
            var samplesA = qa.Sample(in trackA, tick);
            var samplesB = qb.Sample(in trackB, tick);

            while (true)
            {
                var movedA = samplesA.MoveNext();
                Assert.Equal(movedA, samplesB.MoveNext());

                if (!movedA) break;

                Assert.Equal(samplesA.Current.DataOffset, samplesB.Current.DataOffset);
                Assert.Equal(samplesA.Current.Weight, samplesB.Current.Weight);
            }
        }

        FullTransitionRecorder ra = default;
        FullTransitionRecorder rb = default;
        qa.TraverseTransitions(new TimelineSpan(new TimelineIndex(2), 2000, 2600), ref ra);
        qb.TraverseTransitions(new TimelineSpan(new TimelineIndex(2), 2000, 2600), ref rb);
        Assert.Equal(ra.Count, rb.Count);
        Assert.Equal(ra.TickSum, rb.TickSum);
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

    private struct FullTransitionRecorder : IClipTransitionVisitor<TestClip>
    {
        public long Count;
        public long TickSum;
        public int OffsetSum;
        public int PhaseSum;
        public float FactorSum;

        public void OnClipTransition(in TrackInstance track, in ClipTransition transition, in TestClip clip)
        {
            Count++;
            TickSum += transition.GlobalTick;
            OffsetSum += transition.DataOffset;
            PhaseSum += (int)transition.Phase;
        }

        public void OnBlendTransition(
            in TrackInstance track,
            in BlendTransition transition,
            in TestClip clipA,
            in TestClip clipB)
        {
            Count++;
            TickSum += transition.GlobalTick;
            OffsetSum += transition.DataOffsetA + transition.DataOffsetB;
            PhaseSum += (int)transition.Phase;
            FactorSum += transition.Factor;
        }
    }

    /// <summary>
    ///     One authoring shape baked twice: flag off (searched path) and flag
    ///     on (fast-lookup section). Covers exclusive u8 LUT, crossfade u8
    ///     pair LUT with baked factors, loop + pulse, one-frame blend, and a
    ///     duration-3000 timeline that must fall back to the searched path.
    /// </summary>
    private static (TimelineDatabase Searched, TimelineDatabase Fast) BuildParityPair()
    {
        TestClip c1 = new(1);
        TestClip c2 = new(2);
        TestClip c3 = new(3);
        TestClip c4 = new(4);

        DatabaseBuilder Builder()
        {
            DatabaseBuilder builder = new();

            var main = builder.AddTimeline(new TimelineKey(1), 64);
            var loop = builder.AddTimeline(new TimelineKey(2), 10, TimelineFlags.Loop);
            var long1 = builder.AddTimeline(new TimelineKey(3), 3000);

            builder.AddTrack(
                main,
                TestTypes.Clip,
                new BindingId(0),
                TrackMode.Exclusive,
                [
                    Clip.Range(0, 16, in c1),
                    Clip.Range(20, 36, in c2),
                    Clip.Range(48, 64, in c3)
                ]);

            builder.AddTrack(
                main,
                TestTypes.Clip,
                new BindingId(1),
                TrackMode.CrossFade,
                [
                    Clip.Range(0, 24, in c1),
                    Clip.Range(16, 48, in c2),
                    Clip.Range(40, 64, in c3)
                ]);

            // Wide exclusive track: 16 clips over 64 ticks — above the LUT and
            // prefix thresholds, so the narrow u8 LUT and prefix table bake.
            ClipDefinition<TestClip>[] wideExclusive = new ClipDefinition<TestClip>[16];

            for (var i = 0; i < 16; i++)
            {
                var payload = new TestClip(i + 1);
                wideExclusive[i] = Clip.Range(i * 4, i * 4 + 4, in payload);
            }

            builder.AddTrack(main, TestTypes.Clip, new BindingId(2), TrackMode.Exclusive, wideExclusive);

            // Wide crossfade: two lanes of 10 clips each, strict pairwise
            // overlaps (A_i [i*6, i*6+6) vs B_i [i*6+3, i*6+9)).
            List<ClipDefinition<TestClip>> wideCrossFade = [];

            for (var i = 0; i < 10; i++)
            {
                var a = new TestClip(100 + i);
                var b = new TestClip(200 + i);
                wideCrossFade.Add(Clip.Range(i * 6, i * 6 + 6, in a));
                wideCrossFade.Add(Clip.Range(i * 6 + 3, i * 6 + 9, in b));
            }

            builder.AddTrack(
                main,
                TestTypes.Clip,
                new BindingId(3),
                TrackMode.CrossFade,
                CollectionsMarshal.AsSpan(wideCrossFade));

            builder.AddTrack(
                loop,
                TestTypes.Clip,
                new BindingId(0),
                TrackMode.Exclusive,
                [Clip.At(5, in c4)]);

            builder.AddTrack(
                loop,
                TestTypes.Clip,
                new BindingId(1),
                TrackMode.CrossFade,
                [
                    Clip.Range(2, 4, in c1),
                    Clip.Range(3, 5, in c2)
                ]);

            builder.AddTrack(
                long1,
                TestTypes.Clip,
                new BindingId(0),
                TrackMode.Exclusive,
                [
                    Clip.Range(0, 1000, in c1),
                    Clip.Range(2000, 2500, in c2)
                ]);

            return builder;
        }

        return (Builder().Build(false), Builder().Build(true));
    }

    /// <summary>Test-local FNV-1a 64 + header hash rewrite, mirroring the format.</summary>
    private static byte[] WithFixedHash(byte[] blob)
    {
        ulong hash = 14695981039346656037UL;

        for (var i = 28; i < blob.Length; i++)
        {
            hash ^= blob[i];
            hash *= 1099511628211UL;
        }

        BitConverter.GetBytes((uint)hash).CopyTo(blob.AsSpan(24));
        return blob;
    }
}