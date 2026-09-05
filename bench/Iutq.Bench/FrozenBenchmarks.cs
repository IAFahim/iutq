using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Iutq.Core.Baking;
using Iutq.Core.Primitives;
using Iutq.Core.Querying;
using Iutq.Core.Storage;
using CrossFadeSource = Iutq.Frozen.CrossFade;
using PulseSource = Iutq.Frozen.Pulse;
using Timelines = Iutq.Frozen.Timelines;

namespace Iutq.Bench;

// BDN requires instance benchmark methods even when they touch no instance state.
#pragma warning disable CA1822

public struct FrozenParityVisitor : IClipTransitionVisitor<BenchClip>
{
    public long Count;
    public long TickSum;
    public float PayloadSum;

    public void OnClipTransition(in TrackInstance track, in ClipTransition transition, in BenchClip clip)
    {
        Count++;
        TickSum += transition.GlobalTick;
        PayloadSum += clip.A + clip.B;
    }

    public void OnBlendTransition(
        in TrackInstance track,
        in BlendTransition transition,
        in BenchClip clipA,
        in BenchClip clipB)
    {
        Count++;
        TickSum += transition.GlobalTick;
        PayloadSum += clipA.A + clipA.B + clipB.A + clipB.B;
    }
}

/// <summary>
///     Waffle-generated "frozen" twins of the dynamic TimelineBenchmarks scenarios:
///     compile-time baked timelines, tick-to-payload LUTs / comparison chains,
///     prefix-table transitions, zero per-call setup.
/// </summary>
[Config(typeof(TimelineConfig))]
[MemoryDiagnoser]
public class FrozenBenchmarks
{
    private TimelineDatabase _dbCrossFade = null!;
    private TimelineDatabase _dbExclusive = null!;
    private TimelineDatabase _dbPulse = null!;
    private ClipTypeHandle<BenchClip> _handleCrossFade;
    private ClipTypeHandle<BenchClip> _handleExclusive;
    private ClipTypeHandle<BenchClip> _handlePulse;
    private TimelineIndex _crossFadeTimeline;
    private TimelineIndex _exclusiveTimeline0;
    private TimelineIndex _pulseTimeline;
    private TimelineCursor[] _cursors = null!;

    [GlobalSetup]
    public void Setup()
    {
        DatabaseBuilder exclusiveBuilder = new();

        for (var i = 0; i < 8; i++)
        {
            var timeline = exclusiveBuilder.AddTimeline(new TimelineKey(0xA100UL + (ulong)i), 64);
            var clips = new ClipDefinition<BenchClip>[4];

            for (var c = 0; c < 4; c++)
                clips[c] = Clip.Range(c * 16, c * 16 + 16, new BenchClip { A = 10f * i + c, B = 0.5f * c });

            exclusiveBuilder.AddTrack(timeline, BenchTypes.Clip, new BindingId(i), TrackMode.Exclusive, clips);
        }

        _dbExclusive = exclusiveBuilder.Build();
        _handleExclusive = _dbExclusive.Resolve(BenchTypes.Clip);
        _exclusiveTimeline0 = new TimelineIndex(0);

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

        _cursors = new TimelineCursor[8];

        for (var i = 0; i < 8; i++)
            _cursors[i] = new TimelineCursor(new TimelineIndex(i), 24, TimelineDirection.Forward);

        VerifyFrozen();
    }

    /// <summary>Drift guard + frozen-vs-dynamic parity. Aborts the run on any mismatch.</summary>
    private void VerifyFrozen()
    {
        const float tolerance = 1e-4f;

        AssertBlob("Timelines", _dbExclusive, Timelines.FrozenDb.BlobBase64);
        AssertBlob("CrossFade", _dbCrossFade, CrossFadeSource.FrozenDb.BlobBase64);
        AssertBlob("Pulse", _dbPulse, PulseSource.FrozenDb.BlobBase64);

        var view = _dbExclusive.AsView();
        var query = view.Query(_handleExclusive);
        var track = query.Tracks(_exclusiveTimeline0)[0];

        SampleSum dynamicSum = default;
        query.Sample(in track, 24, ref dynamicSum);
        SampleSum frozenSum = default;
        Timelines.Timeline0.Sample0<BenchClip, SampleSum>(24, ref frozenSum);
        AssertApprox("SampleFusedOneTrack.A", dynamicSum.A, frozenSum.A, tolerance);
        AssertApprox("SampleFusedOneTrack.B", dynamicSum.B, frozenSum.B, tolerance);

        SampleSum dynamicEight = default;
        query.Sample(_cursors.AsSpan(), ref dynamicEight);
        SampleSum frozenEight = default;
        Timelines.Timeline0.Sample0<BenchClip, SampleSum>(24, ref frozenEight);
        Timelines.Timeline1.Sample0<BenchClip, SampleSum>(24, ref frozenEight);
        Timelines.Timeline2.Sample0<BenchClip, SampleSum>(24, ref frozenEight);
        Timelines.Timeline3.Sample0<BenchClip, SampleSum>(24, ref frozenEight);
        Timelines.Timeline4.Sample0<BenchClip, SampleSum>(24, ref frozenEight);
        Timelines.Timeline5.Sample0<BenchClip, SampleSum>(24, ref frozenEight);
        Timelines.Timeline6.Sample0<BenchClip, SampleSum>(24, ref frozenEight);
        Timelines.Timeline7.Sample0<BenchClip, SampleSum>(24, ref frozenEight);
        AssertApprox("SampleEightCursors.A", dynamicEight.A, frozenEight.A, tolerance);
        AssertApprox("SampleEightCursors.B", dynamicEight.B, frozenEight.B, tolerance);

        var crossView = _dbCrossFade.AsView();
        var crossQuery = crossView.Query(_handleCrossFade);
        var crossTrack = crossQuery.Tracks(_crossFadeTimeline)[0];
        SampleSum dynamicBlend = default;
        crossQuery.Sample(in crossTrack, 18, ref dynamicBlend);
        SampleSum frozenBlend = default;
        CrossFadeSource.Timeline0.Sample0<BenchClip, SampleSum>(18, ref frozenBlend);
        AssertApprox("SampleCrossFade.A", dynamicBlend.A, frozenBlend.A, tolerance);
        AssertApprox("SampleCrossFade.B", dynamicBlend.B, frozenBlend.B, tolerance);

        var pulseView = _dbPulse.AsView();
        var pulseQuery = pulseView.Query(_handlePulse);

        CompareTraverse(pulseQuery, "TraverseForwardOneTick", 4, 5, tolerance);
        CompareTraverse(pulseQuery, "TraverseForwardFullLoop", 0, 63, tolerance);
        CompareTraverse(pulseQuery, "TraverseRewindTwentyFive", 25, 5, tolerance);
    }

    private void CompareTraverse(ClipQuery<BenchClip> pulseQuery, string scenario, long previous, long current, float tolerance)
    {
        var span = new TimelineSpan(_pulseTimeline, previous, current);

        TransitionCounter dynamicCounter = default;
        var dynamicReturned = pulseQuery.TraverseTransitions(in span, ref dynamicCounter);
        FrozenParityVisitor dynamicParity = default;
        pulseQuery.TraverseTransitions(in span, ref dynamicParity);

        TransitionCounter frozenCounter = default;
        var frozenReturned = PulseSource.Timeline0.Traverse<BenchClip, TransitionCounter>(in span, ref frozenCounter);
        FrozenParityVisitor frozenParity = default;
        PulseSource.Timeline0.Traverse<BenchClip, FrozenParityVisitor>(in span, ref frozenParity);

        if (dynamicReturned + dynamicCounter.Count != frozenReturned + frozenCounter.Count)
            throw new InvalidOperationException(
                $"{scenario}: dynamic {dynamicReturned + dynamicCounter.Count} vs frozen {frozenReturned + frozenCounter.Count}.");

        if (dynamicParity.Count != frozenParity.Count || dynamicParity.TickSum != frozenParity.TickSum)
            throw new InvalidOperationException(
                $"{scenario}: parity mismatch (count {dynamicParity.Count}/{frozenParity.Count}, " +
                $"tickSum {dynamicParity.TickSum}/{frozenParity.TickSum}).");

        if (MathF.Abs(dynamicParity.PayloadSum - frozenParity.PayloadSum) > tolerance)
            throw new InvalidOperationException(
                $"{scenario}: payload parity mismatch {dynamicParity.PayloadSum} vs {frozenParity.PayloadSum}.");
    }

    private static void AssertBlob(string source, TimelineDatabase database, string base64)
    {
        var committed = Convert.FromBase64String(base64);

        if (!database.ToArray().AsSpan().SequenceEqual(committed))
            throw new InvalidOperationException(
                $"frozen drift: {source}.iutq does not match the runtime-built database. Re-run tools/BakeBlob.");
    }

    private static void AssertApprox(string scenario, float actual, float expected, float tolerance)
    {
        if (MathF.Abs(actual - expected) > tolerance)
            throw new InvalidOperationException($"{scenario}: {actual} vs {expected}.");
    }

    [Benchmark]
    public float FrozenSampleFusedOneTrack()
    {
        SampleSum acc = default;
        Timelines.Timeline0.Sample0<BenchClip, SampleSum>(24, ref acc);
        return acc.A + acc.B;
    }

    [Benchmark]
    public float FrozenSampleEightCursors()
    {
        SampleSum sum = default;
        Timelines.Timeline0.Sample0<BenchClip, SampleSum>(24, ref sum);
        Timelines.Timeline1.Sample0<BenchClip, SampleSum>(24, ref sum);
        Timelines.Timeline2.Sample0<BenchClip, SampleSum>(24, ref sum);
        Timelines.Timeline3.Sample0<BenchClip, SampleSum>(24, ref sum);
        Timelines.Timeline4.Sample0<BenchClip, SampleSum>(24, ref sum);
        Timelines.Timeline5.Sample0<BenchClip, SampleSum>(24, ref sum);
        Timelines.Timeline6.Sample0<BenchClip, SampleSum>(24, ref sum);
        Timelines.Timeline7.Sample0<BenchClip, SampleSum>(24, ref sum);
        return sum.A + sum.B;
    }

    [Benchmark]
    public float FrozenSampleCrossFade()
    {
        SampleSum acc = default;
        CrossFadeSource.Timeline0.Sample0<BenchClip, SampleSum>(18, ref acc);
        return acc.A + acc.B;
    }

    [Benchmark]
    public long FrozenTraverseForwardOneTick()
    {
        TransitionCounter counter = default;
        return PulseSource.Timeline0.Traverse<BenchClip, TransitionCounter>(
            new TimelineSpan(_pulseTimeline, 4, 5), ref counter) + counter.Count;
    }

    [Benchmark]
    public long FrozenTraverseForwardFullLoop()
    {
        TransitionCounter counter = default;
        return PulseSource.Timeline0.Traverse<BenchClip, TransitionCounter>(
            new TimelineSpan(_pulseTimeline, 0, 63), ref counter) + counter.Count;
    }

    [Benchmark]
    public long FrozenTraverseRewindTwentyFive()
    {
        TransitionCounter counter = default;
        return PulseSource.Timeline0.Traverse<BenchClip, TransitionCounter>(
            new TimelineSpan(_pulseTimeline, 25, 5), ref counter) + counter.Count;
    }
}
