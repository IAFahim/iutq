using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Iutq.Core;
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

    public void Visit(in TrackInstance track, in ClipHit hit, in BenchClip clip)
    {
        A += clip.A * hit.Weight;
        B += clip.B * hit.Weight;
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

    public void Clip(in TrackInstance track, in ClipTransition transition, in BenchClip clip) => Count++;

    public void Blend(in TrackInstance track, in BlendTransition transition, in BenchClip clipA, in BenchClip clipB) => Count++;
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
    private TimelineDatabase _dbExclusive = null!;
    private TimelineDatabase _dbCrossFade = null!;
    private TimelineDatabase _dbPulse = null!;
    private ClipHandle<BenchClip> _handleExclusive;
    private ClipHandle<BenchClip> _handleCrossFade;
    private ClipHandle<BenchClip> _handlePulse;
    private TimelineId[] _timelineIds = null!;
    private TimelineId _crossFadeTimeline;
    private TimelineId _pulseTimeline;
    private TimelineCursor[] _singleCursor = null!;
    private TimelineCursor[] _eightCursors = null!;

    [GlobalSetup]
    public void Setup()
    {
        DatabaseBuilder exclusiveBuilder = new();
        _timelineIds = new TimelineId[8];

        for (int i = 0; i < 8; i++)
        {
            TimelineId timeline = exclusiveBuilder.AddTimeline(new TimelineKey(0xA100UL + (ulong)i), 64);
            _timelineIds[i] = timeline;
            ClipDefinition<BenchClip>[] clips = new ClipDefinition<BenchClip>[4];

            for (int c = 0; c < 4; c++)
            {
                clips[c] = Clip.Range(c * 16, c * 16 + 16, new BenchClip { A = 10f * i + c, B = 0.5f * c });
            }

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
            Clip.Range(40, 64, new BenchClip { A = 3f, B = 0.3f }),
        ];
        crossFadeBuilder.AddTrack(_crossFadeTimeline, BenchTypes.Clip, new BindingId(0), TrackMode.CrossFade, crossFadeClips);
        _dbCrossFade = crossFadeBuilder.Build();
        _handleCrossFade = _dbCrossFade.Resolve(BenchTypes.Clip);

        DatabaseBuilder pulseBuilder = new();
        _pulseTimeline = pulseBuilder.AddTimeline(new TimelineKey(0xC1), 64, TimelineFlags.Loop);
        ClipDefinition<BenchClip>[] pulseClips =
        [
            Clip.At(5, new BenchClip { A = 5f, B = 0.5f }),
            Clip.Range(8, 20, new BenchClip { A = 8f, B = 0.8f }),
            Clip.At(21, new BenchClip { A = 21f, B = 2.1f }),
            Clip.At(40, new BenchClip { A = 40f, B = 4f }),
        ];
        pulseBuilder.AddTrack(_pulseTimeline, BenchTypes.Clip, new BindingId(0), TrackMode.Exclusive, pulseClips);
        _dbPulse = pulseBuilder.Build();
        _handlePulse = _dbPulse.Resolve(BenchTypes.Clip);

        _singleCursor = [new TimelineCursor(_timelineIds[0], 24, TimelineDirection.Forward)];
        _eightCursors = new TimelineCursor[8];

        for (int i = 0; i < 8; i++)
        {
            _eightCursors[i] = new TimelineCursor(_timelineIds[i], 24, TimelineDirection.Forward);
        }
    }

    [Benchmark]
    public float FrameExclusive()
    {
        DatabaseView view = _dbExclusive.AsView();
        ClipQuery<BenchClip> query = view.Query(_handleExclusive);
        TrackInstance track = query.Tracks(_timelineIds[0])[0];
        ClipFrameEnumerator frame = query.Frame(in track, 24, TimelineDirection.Forward);
        WeightedSum sum = default;

        while (frame.MoveNext())
        {
            ref readonly BenchClip clip = ref query.Data(frame.Current.DataOffset);
            sum.A += clip.A * frame.Current.Weight;
            sum.B += clip.B * frame.Current.Weight;
        }

        return sum.A + sum.B;
    }

    [Benchmark]
    public float FrameCrossFade()
    {
        DatabaseView view = _dbCrossFade.AsView();
        ClipQuery<BenchClip> query = view.Query(_handleCrossFade);
        TrackInstance track = query.Tracks(_crossFadeTimeline)[0];
        ClipFrameEnumerator frame = query.Frame(in track, 18, TimelineDirection.Forward);
        WeightedSum sum = default;

        while (frame.MoveNext())
        {
            ref readonly BenchClip clip = ref query.Data(frame.Current.DataOffset);
            sum.A += clip.A * frame.Current.Weight;
            sum.B += clip.B * frame.Current.Weight;
        }

        return sum.A + sum.B;
    }

    [Benchmark]
    public float SampleExclusive()
    {
        DatabaseView view = _dbExclusive.AsView();
        ClipQuery<BenchClip> query = view.Query(_handleExclusive);
        TrackInstance track = query.Tracks(_timelineIds[0])[0];
        ClipSampleEnumerator samples = query.Sample(in track, 24);
        float sum = 0f;

        while (samples.MoveNext())
        {
            ClipSample sample = samples.Current;
            ref readonly BenchClip clip = ref query.Data(sample.DataOffset);
            sum += (clip.A + clip.B) * sample.Weight;
        }

        return sum;
    }

    [Benchmark]
    public float SampleCrossFade()
    {
        DatabaseView view = _dbCrossFade.AsView();
        ClipQuery<BenchClip> query = view.Query(_handleCrossFade);
        TrackInstance track = query.Tracks(_crossFadeTimeline)[0];
        ClipSampleEnumerator samples = query.Sample(in track, 18);
        float sum = 0f;

        while (samples.MoveNext())
        {
            ClipSample sample = samples.Current;
            ref readonly BenchClip clip = ref query.Data(sample.DataOffset);
            sum += (clip.A + clip.B) * sample.Weight;
        }

        return sum;
    }

    [Benchmark]
    public float SampleFusedOneTrack()
    {
        DatabaseView view = _dbExclusive.AsView();
        ClipQuery<BenchClip> query = view.Query(_handleExclusive);
        TrackInstance track = query.Tracks(_timelineIds[0])[0];
        SampleSum acc = default;
        query.Sample(in track, 24, ref acc);
        return acc.A + acc.B;
    }

    [Benchmark]
    public float SampleEightCursors()
    {
        DatabaseView view = _dbExclusive.AsView();
        ClipQuery<BenchClip> query = view.Query(_handleExclusive);
        SampleSum sum = default;
        query.Sample(_eightCursors.AsSpan(), ref sum);
        return sum.A + sum.B;
    }

    [Benchmark]
    public float VisitOneCursor()
    {
        DatabaseView view = _dbExclusive.AsView();
        ClipQuery<BenchClip> query = view.Query(_handleExclusive);
        WeightedSum sum = default;
        query.Visit(_singleCursor.AsSpan(), ref sum);
        return sum.A + sum.B;
    }

    [Benchmark]
    public float VisitEightCursors()
    {
        DatabaseView view = _dbExclusive.AsView();
        ClipQuery<BenchClip> query = view.Query(_handleExclusive);
        WeightedSum sum = default;
        query.Visit(_eightCursors.AsSpan(), ref sum);
        return sum.A + sum.B;
    }

    [Benchmark]
    public long TraverseForwardOneTick()
    {
        DatabaseView view = _dbPulse.AsView();
        ClipQuery<BenchClip> query = view.Query(_handlePulse);
        TimelineSpan span = new(_pulseTimeline, 4, 5);
        TransitionCounter counter = default;
        return query.TraverseTransitions(in span, ref counter) + counter.Count;
    }

    [Benchmark]
    public long TraverseForwardFullLoop()
    {
        DatabaseView view = _dbPulse.AsView();
        ClipQuery<BenchClip> query = view.Query(_handlePulse);
        TimelineSpan span = new(_pulseTimeline, 0, 63);
        TransitionCounter counter = default;
        return query.TraverseTransitions(in span, ref counter) + counter.Count;
    }

    [Benchmark]
    public long TraverseRewindTwentyFive()
    {
        DatabaseView view = _dbPulse.AsView();
        ClipQuery<BenchClip> query = view.Query(_handlePulse);
        TimelineSpan span = new(_pulseTimeline, 25, 5);
        TransitionCounter counter = default;
        return query.TraverseTransitions(in span, ref counter) + counter.Count;
    }

    [Benchmark]
    public float QuerySetupOnly()
    {
        DatabaseView view = _dbExclusive.AsView();
        ClipQuery<BenchClip> query = view.Query(_handleExclusive);
        return query.Tracks(_timelineIds[0]).Length;
    }
}
