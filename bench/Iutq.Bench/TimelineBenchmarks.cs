using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Iutq.Core.Baking;
using Iutq.Core.Primitives;
using Iutq.Core.Querying;
using Iutq.Core.Storage;
using Perfolizer.Horology;

namespace Iutq.Bench;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BenchClip
{
    public float A;
    public float B;
}

public static class BenchTypes
{
    public static readonly ClipType<BenchClip> Clip = new(0x5100000000000001UL);
}

public struct WeightedSum : IClipFrameVisitor<BenchClip>
{
    public float A;
    public float B;

    public void Visit(in TrackInstance track, in ClipFrame frame, in BenchClip clip)
    {
        A += clip.A * frame.Weight;
        B += clip.B * frame.Weight;
    }
}

public struct SampleSum : IClipSampleVisitor<BenchClip>
{
    public float A;
    public float B;

    public void Sample(in TrackInstance track, in BenchClip clip, float weight)
    {
        A += clip.A * weight;
        B += clip.B * weight;
    }
}

public struct TransitionCounter : IClipTransitionVisitor<BenchClip>
{
    public int Count;

    public void OnClipTransition(in TrackInstance track, in ClipTransition transition, in BenchClip clip)
    {
        Count++;
    }

    public void OnBlendTransition(in TrackInstance track, in BlendTransition transition, in BenchClip clipA, in BenchClip clipB)
    {
        Count++;
    }
}

public sealed class TimelineConfig : ManualConfig
{
    public TimelineConfig()
    {
        AddJob(Job.Default
            .WithWarmupCount(3)
            .WithIterationTime(TimeInterval.FromMilliseconds(100))
            .WithIterationCount(15));
        AddColumn(StatisticColumn.Median);
    }
}

[Config(typeof(TimelineConfig))]
[MemoryDiagnoser]
public class TimelineBenchmarks
{
    private TimelineIndex _crossFadeTimeline;
    private TimelineDatabase _dbCrossFade = null!;
    private TimelineDatabase _dbExclusive = null!;
    private TimelineDatabase _dbPulse = null!;
    private TimelineCursor[] _eightCursors = null!;
    private ClipTypeHandle<BenchClip> _handleCrossFade;
    private ClipTypeHandle<BenchClip> _handleExclusive;
    private ClipTypeHandle<BenchClip> _handlePulse;
    private TimelineIndex _pulseTimeline;
    private TimelineCursor[] _singleCursor = null!;
    private TimelineIndex[] _timelineIndexs = null!;

    [GlobalSetup]
    public void Setup()
    {
        DatabaseBuilder exclusiveBuilder = new();
        _timelineIndexs = new TimelineIndex[8];

        for (var i = 0; i < 8; i++)
        {
            var timeline = exclusiveBuilder.AddTimeline(new TimelineKey(0xA100UL + (ulong)i), 64);
            _timelineIndexs[i] = timeline;
            var clips = new ClipDefinition<BenchClip>[4];

            for (var c = 0; c < 4; c++)
                clips[c] = Clip.Range(c * 16, c * 16 + 16, new BenchClip { A = 10f * i + c, B = 0.5f * c });

            exclusiveBuilder.AddTrack(timeline, BenchTypes.Clip, new BindingId(i), TrackMode.Exclusive, clips);
        }

        _dbExclusive = exclusiveBuilder.Build();
        _handleExclusive = _dbExclusive.Resolve(BenchTypes.Clip);

        DatabaseBuilder crossFadeBuilder = new();
        _crossFadeTimeline = crossFadeBuilder.AddTimeline(new TimelineKey(0xB1), 64);
        ClipDefinition<BenchClip>[] crossFadeClips =
        [
            Clip.Range(0, 24, new BenchClip { A = 1f, B = 0.1f }),
            Clip.Range(16, 48, new BenchClip { A = 2f, B = 0.2f }),
            Clip.Range(40, 64, new BenchClip { A = 3f, B = 0.3f })
        ];
        crossFadeBuilder.AddTrack(_crossFadeTimeline, BenchTypes.Clip, new BindingId(0), TrackMode.CrossFade,
            crossFadeClips);
        _dbCrossFade = crossFadeBuilder.Build();
        _handleCrossFade = _dbCrossFade.Resolve(BenchTypes.Clip);

        DatabaseBuilder pulseBuilder = new();
        _pulseTimeline = pulseBuilder.AddTimeline(new TimelineKey(0xC1), 64, TimelineFlags.Loop);
        ClipDefinition<BenchClip>[] pulseClips =
        [
            Clip.At(5, new BenchClip { A = 5f, B = 0.5f }),
            Clip.Range(8, 20, new BenchClip { A = 8f, B = 0.8f }),
            Clip.At(21, new BenchClip { A = 21f, B = 2.1f }),
            Clip.At(40, new BenchClip { A = 40f, B = 4f })
        ];
        pulseBuilder.AddTrack(_pulseTimeline, BenchTypes.Clip, new BindingId(0), TrackMode.Exclusive, pulseClips);
        _dbPulse = pulseBuilder.Build();
        _handlePulse = _dbPulse.Resolve(BenchTypes.Clip);

        _singleCursor = [new TimelineCursor(_timelineIndexs[0], 24, TimelineDirection.Forward)];
        _eightCursors = new TimelineCursor[8];

        for (var i = 0; i < 8; i++)
            _eightCursors[i] = new TimelineCursor(_timelineIndexs[i], 24, TimelineDirection.Forward);

        VerifyProtocol();
    }

    private void VerifyProtocol()
    {
        const float tolerance = 1e-4f;

        var view = _dbExclusive.AsView();
        var query = view.Query(_handleExclusive);
        var track = query.Tracks(_timelineIndexs[0])[0];

        WeightedSum weighted = default;
        var frame = query.VisitFrames(in track, 24, TimelineDirection.Forward);
        while (frame.MoveNext())
        {
            ref readonly var clip = ref query.Data(frame.Current.DataOffset);
            weighted.A += clip.A * frame.Current.Weight;
            weighted.B += clip.B * frame.Current.Weight;
        }

        AssertApprox("FrameExclusive", weighted.A + weighted.B, 1.5f, tolerance);

        var crossView = _dbCrossFade.AsView();
        var crossQuery = crossView.Query(_handleCrossFade);
        var crossTrack = crossQuery.Tracks(_crossFadeTimeline)[0];
        weighted = default;
        frame = crossQuery.VisitFrames(in crossTrack, 18, TimelineDirection.Forward);
        while (frame.MoveNext())
        {
            ref readonly var clip = ref crossQuery.Data(frame.Current.DataOffset);
            weighted.A += clip.A * frame.Current.Weight;
            weighted.B += clip.B * frame.Current.Weight;
        }

        AssertApprox("FrameCrossFade", weighted.A + weighted.B, 9.9f / 7f, tolerance);

        var sum = 0f;
        var samples = query.Sample(in track, 24);
        while (samples.MoveNext())
        {
            var sample = samples.Current;
            ref readonly var clip = ref query.Data(sample.DataOffset);
            sum += (clip.A + clip.B) * sample.Weight;
        }

        AssertApprox("SampleExclusive", sum, 1.5f, tolerance);

        sum = 0f;
        samples = crossQuery.Sample(in crossTrack, 18);
        while (samples.MoveNext())
        {
            var sample = samples.Current;
            ref readonly var clip = ref crossQuery.Data(sample.DataOffset);
            sum += (clip.A + clip.B) * sample.Weight;
        }

        AssertApprox("SampleCrossFade", sum, 9.9f / 7f, tolerance);

        SampleSum fused = default;
        query.Sample(in track, 24, ref fused);
        AssertApprox("SampleFusedOneTrack", fused.A + fused.B, 1.5f, tolerance);

        fused = default;
        query.Sample(_eightCursors.AsSpan(), ref fused);
        AssertApprox("SampleEightCursors", fused.A + fused.B, 292f, tolerance);

        weighted = default;
        query.VisitFrames(_singleCursor.AsSpan(), ref weighted);
        AssertApprox("VisitOneCursor", weighted.A + weighted.B, 1.5f, tolerance);

        weighted = default;
        query.VisitFrames(_eightCursors.AsSpan(), ref weighted);
        AssertApprox("VisitEightCursors", weighted.A + weighted.B, 292f, tolerance);

        var pulseView = _dbPulse.AsView();
        var pulseQuery = pulseView.Query(_handlePulse);
        AssertEqual("TraverseForwardOneTick", CountTransitions(pulseQuery, new TimelineSpan(_pulseTimeline, 4, 5)), 1);
        AssertEqual("TraverseForwardFullLoop", CountTransitions(pulseQuery, new TimelineSpan(_pulseTimeline, 0, 63)),
            5);
        AssertEqual("TraverseRewindTwentyFive", CountTransitions(pulseQuery, new TimelineSpan(_pulseTimeline, 25, 5)),
            4);

        AssertEqual("QuerySetupOnly", query.Tracks(_timelineIndexs[0]).Length, 1);
    }

    private static long CountTransitions(ClipQuery<BenchClip> query, TimelineSpan span)
    {
        TransitionCounter counter = default;
        return query.TraverseTransitions(in span, ref counter);
    }

    private static void AssertApprox(string scenario, float actual, float expected, float tolerance)
    {
        if (MathF.Abs(actual - expected) > tolerance)
            throw new InvalidOperationException(
                $"protocol verify failed: {scenario} produced {actual}, expected {expected}.");
    }

    private static void AssertEqual(string scenario, long actual, long expected)
    {
        if (actual != expected)
            throw new InvalidOperationException(
                $"protocol verify failed: {scenario} produced {actual}, expected {expected}.");
    }

    [Benchmark]
    public float FrameExclusive()
    {
        var view = _dbExclusive.AsView();
        var query = view.Query(_handleExclusive);
        var track = query.Tracks(_timelineIndexs[0])[0];
        var frame = query.VisitFrames(in track, 24, TimelineDirection.Forward);
        WeightedSum sum = default;

        while (frame.MoveNext())
        {
            ref readonly var clip = ref query.Data(frame.Current.DataOffset);
            sum.A += clip.A * frame.Current.Weight;
            sum.B += clip.B * frame.Current.Weight;
        }

        return sum.A + sum.B;
    }

    [Benchmark]
    public float FrameCrossFade()
    {
        var view = _dbCrossFade.AsView();
        var query = view.Query(_handleCrossFade);
        var track = query.Tracks(_crossFadeTimeline)[0];
        var frame = query.VisitFrames(in track, 18, TimelineDirection.Forward);
        WeightedSum sum = default;

        while (frame.MoveNext())
        {
            ref readonly var clip = ref query.Data(frame.Current.DataOffset);
            sum.A += clip.A * frame.Current.Weight;
            sum.B += clip.B * frame.Current.Weight;
        }

        return sum.A + sum.B;
    }

    [Benchmark]
    public float SampleExclusive()
    {
        var view = _dbExclusive.AsView();
        var query = view.Query(_handleExclusive);
        var track = query.Tracks(_timelineIndexs[0])[0];
        var samples = query.Sample(in track, 24);
        var sum = 0f;

        while (samples.MoveNext())
        {
            var sample = samples.Current;
            ref readonly var clip = ref query.Data(sample.DataOffset);
            sum += (clip.A + clip.B) * sample.Weight;
        }

        return sum;
    }

    [Benchmark]
    public float SampleCrossFade()
    {
        var view = _dbCrossFade.AsView();
        var query = view.Query(_handleCrossFade);
        var track = query.Tracks(_crossFadeTimeline)[0];
        var samples = query.Sample(in track, 18);
        var sum = 0f;

        while (samples.MoveNext())
        {
            var sample = samples.Current;
            ref readonly var clip = ref query.Data(sample.DataOffset);
            sum += (clip.A + clip.B) * sample.Weight;
        }

        return sum;
    }

    [Benchmark]
    public float SampleFusedOneTrack()
    {
        var view = _dbExclusive.AsView();
        var query = view.Query(_handleExclusive);
        var track = query.Tracks(_timelineIndexs[0])[0];
        SampleSum acc = default;
        query.Sample(in track, 24, ref acc);
        return acc.A + acc.B;
    }

    [Benchmark]
    public float SampleEightCursors()
    {
        var view = _dbExclusive.AsView();
        var query = view.Query(_handleExclusive);
        SampleSum sum = default;
        query.Sample(_eightCursors.AsSpan(), ref sum);
        return sum.A + sum.B;
    }

    [Benchmark]
    public float VisitOneCursor()
    {
        var view = _dbExclusive.AsView();
        var query = view.Query(_handleExclusive);
        WeightedSum sum = default;
        query.VisitFrames(_singleCursor.AsSpan(), ref sum);
        return sum.A + sum.B;
    }

    [Benchmark]
    public float VisitEightCursors()
    {
        var view = _dbExclusive.AsView();
        var query = view.Query(_handleExclusive);
        WeightedSum sum = default;
        query.VisitFrames(_eightCursors.AsSpan(), ref sum);
        return sum.A + sum.B;
    }

    [Benchmark]
    public long TraverseForwardOneTick()
    {
        var view = _dbPulse.AsView();
        var query = view.Query(_handlePulse);
        TimelineSpan span = new(_pulseTimeline, 4, 5);
        TransitionCounter counter = default;
        return query.TraverseTransitions(in span, ref counter) + counter.Count;
    }

    [Benchmark]
    public long TraverseForwardFullLoop()
    {
        var view = _dbPulse.AsView();
        var query = view.Query(_handlePulse);
        TimelineSpan span = new(_pulseTimeline, 0, 63);
        TransitionCounter counter = default;
        return query.TraverseTransitions(in span, ref counter) + counter.Count;
    }

    [Benchmark]
    public long TraverseRewindTwentyFive()
    {
        var view = _dbPulse.AsView();
        var query = view.Query(_handlePulse);
        TimelineSpan span = new(_pulseTimeline, 25, 5);
        TransitionCounter counter = default;
        return query.TraverseTransitions(in span, ref counter) + counter.Count;
    }

    [Benchmark]
    public float QuerySetupOnly()
    {
        var view = _dbExclusive.AsView();
        var query = view.Query(_handleExclusive);
        return query.Tracks(_timelineIndexs[0]).Length;
    }
}