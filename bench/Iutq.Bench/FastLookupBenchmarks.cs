using BenchmarkDotNet.Attributes;
using Iutq.Core.Baking;
using Iutq.Core.Primitives;
using Iutq.Core.Querying;
using Iutq.Core.Storage;

namespace Iutq.Bench;

/// <summary>
///     Twin scenarios of <see cref="TimelineBenchmarks" /> against a database
///     baked with the optional fast-lookup section (tick-to-clip LUTs, boundary
///     prefix tables, baked crossfade factors). GlobalSetup verifies bit-exact
///     parity against the same content baked without the flag, so a semantic
///     drift aborts the run before anything is measured.
/// </summary>
[Config(typeof(TimelineConfig))]
[MemoryDiagnoser]
public class FastLookupBenchmarks
{
    private TimelineDatabase _dbCrossFade = null!;
    private TimelineDatabase _dbExclusive = null!;
    private TimelineDatabase _dbExclusiveSearched = null!;
    private TimelineDatabase _dbPulse = null!;
    private TimelineDatabase _dbWide = null!;
    private TimelineDatabase _dbWideSearched = null!;
    private ClipTypeHandle<BenchClip> _handleCrossFade;
    private ClipTypeHandle<BenchClip> _handleExclusive;
    private ClipTypeHandle<BenchClip> _handlePulse;
    private ClipTypeHandle<BenchClip> _handleWide;
    private TimelineIndex _crossFadeTimeline;
    private TimelineIndex _pulseTimeline;
    private TimelineIndex _wideTimeline;
    private TimelineIndex[] _timelines = null!;
    private TimelineCursor[] _eightCursors = null!;
    private TimelineCursor[] _singleCursor = null!;

    [GlobalSetup]
    public void Setup()
    {
        DatabaseBuilder ExclusiveBuilder()
        {
            DatabaseBuilder builder = new();
            _timelines = new TimelineIndex[8];

            for (var i = 0; i < 8; i++)
            {
                var timeline = builder.AddTimeline(new TimelineKey(0xA100UL + (ulong)i), 64);
                _timelines[i] = timeline;
                var clips = new ClipDefinition<BenchClip>[4];

                for (var c = 0; c < 4; c++)
                    clips[c] = Clip.Range(c * 16, c * 16 + 16, new BenchClip { A = 10f * i + c, B = 0.5f * c });

                builder.AddTrack(timeline, BenchTypes.Clip, new BindingId(i), TrackMode.Exclusive, clips);
            }

            return builder;
        }

        _dbExclusive = ExclusiveBuilder().Build(true);
        _dbExclusiveSearched = ExclusiveBuilder().Build(false);
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
        _dbCrossFade = crossFadeBuilder.Build(true);
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
        _dbPulse = pulseBuilder.Build(true);
        _handlePulse = _dbPulse.Resolve(BenchTypes.Clip);

        DatabaseBuilder WideBuilder()
        {
            DatabaseBuilder builder = new();
            _wideTimeline = builder.AddTimeline(new TimelineKey(0xD1), 256);
            ClipDefinition<BenchClip>[] wideClips = new ClipDefinition<BenchClip>[128];

            for (var i = 0; i < 128; i++)
                wideClips[i] = Clip.Range(i * 2, i * 2 + 2, new BenchClip { A = i, B = 0.25f });

            builder.AddTrack(_wideTimeline, BenchTypes.Clip, new BindingId(0), TrackMode.Exclusive, wideClips);
            return builder;
        }

        _dbWide = WideBuilder().Build(true);
        _dbWideSearched = WideBuilder().Build(false);
        _handleWide = _dbWide.Resolve(BenchTypes.Clip);

        _singleCursor = [new TimelineCursor(_timelines[0], 24, TimelineDirection.Forward)];
        _eightCursors = new TimelineCursor[8];

        for (var i = 0; i < 8; i++)
            _eightCursors[i] = new TimelineCursor(_timelines[i], 24, TimelineDirection.Forward);

        VerifyParity();
    }

    /// <summary>Bit-exact searched-vs-fast parity over every tick, frame direction and span.</summary>
    private void VerifyParity()
    {
        var searchedView = _dbExclusiveSearched.AsView();
        var searched = searchedView.Query(_handleExclusive);
        var fastView = _dbExclusive.AsFastView();
        var fast = fastView.Query(_handleExclusive);

        for (var timeline = 0; timeline < fastView.View.Timelines.Length; timeline++)
        {
            var tracksSearched = searched.Tracks(new TimelineIndex(timeline));
            var tracksFast = fast.Tracks(new TimelineIndex(timeline));

            for (var t = 0; t < tracksFast.Length; t++)
            {
                ref readonly var trackSearched = ref tracksSearched[t];
                ref readonly var trackFast = ref tracksFast[t];

                for (var tick = 0; tick < fastView.View.Timelines[timeline].Duration; tick++)
                {
                    var a = searched.Sample(in trackSearched, tick);
                    var b = fast.Sample(in trackFast, tick);

                    while (a.MoveNext())
                    {
                        if (!b.MoveNext() ||
                            a.Current.DataOffset != b.Current.DataOffset ||
                            a.Current.Weight != b.Current.Weight)
                            throw new InvalidOperationException($"fast-lookup sample drift at timeline {timeline} tick {tick}.");
                    }

                    if (b.MoveNext())
                        throw new InvalidOperationException($"fast-lookup sample drift at timeline {timeline} tick {tick}.");

                    foreach (var direction in new[] { TimelineDirection.Forward, TimelineDirection.Reverse })
                    {
                        var fa = searched.VisitFrames(in trackSearched, tick, direction);
                        var fb = fast.VisitFrames(in trackFast, tick, direction);

                        while (fa.MoveNext())
                        {
                            if (!fb.MoveNext() ||
                                fa.Current.DataOffset != fb.Current.DataOffset ||
                                fa.Current.Weight != fb.Current.Weight ||
                                fa.Current.BlendFactor != fb.Current.BlendFactor ||
                                fa.Current.BlendPhase != fb.Current.BlendPhase ||
                                fa.Current.Phase != fb.Current.Phase)
                                throw new InvalidOperationException($"fast-lookup frame drift at timeline {timeline} tick {tick}.");
                        }

                        if (fb.MoveNext())
                            throw new InvalidOperationException($"fast-lookup frame drift at timeline {timeline} tick {tick}.");
                    }
                }
            }
        }

        var crossSearched = _dbCrossFade.AsView().Query(_handleCrossFade);
        var crossFast = _dbCrossFade.AsFastView().Query(_handleCrossFade);
        var crossTrackSearched = crossSearched.Tracks(_crossFadeTimeline)[0];
        var crossTrackFast = crossFast.Tracks(_crossFadeTimeline)[0];

        for (var tick = 0; tick < 64; tick++)
        {
            var a = crossSearched.Sample(in crossTrackSearched, tick);
            var b = crossFast.Sample(in crossTrackFast, tick);

            while (a.MoveNext())
            {
                if (!b.MoveNext() ||
                    a.Current.DataOffset != b.Current.DataOffset ||
                    a.Current.Weight != b.Current.Weight)
                    throw new InvalidOperationException($"fast-lookup crossfade sample drift at tick {tick}.");
            }

            if (b.MoveNext())
                throw new InvalidOperationException($"fast-lookup crossfade sample drift at tick {tick}.");
        }

        var pulseSearched = _dbPulse.AsView().Query(_handlePulse);
        var pulseFast = _dbPulse.AsFastView().Query(_handlePulse);

        CompareTraverse(pulseSearched, pulseFast, 4, 5);
        CompareTraverse(pulseSearched, pulseFast, 0, 63);
        CompareTraverse(pulseSearched, pulseFast, 25, 5);
        CompareTraverse(pulseSearched, pulseFast, 3, -2);

        // Wide track: u8 LUT + prefix table paths, every tick and direction.
        var wideSearched = _dbWideSearched.AsView().Query(_handleWide);
        var wideFast = _dbWide.AsFastView().Query(_handleWide);
        var wideTrackSearched = wideSearched.Tracks(_wideTimeline)[0];
        var wideTrackFast = wideFast.Tracks(_wideTimeline)[0];

        for (var tick = 0; tick < 256; tick++)
        {
            var a = wideSearched.Sample(in wideTrackSearched, tick);
            var b = wideFast.Sample(in wideTrackFast, tick);

            while (a.MoveNext())
            {
                if (!b.MoveNext() ||
                    a.Current.DataOffset != b.Current.DataOffset ||
                    a.Current.Weight != b.Current.Weight)
                    throw new InvalidOperationException($"fast-lookup wide sample drift at tick {tick}.");
            }

            if (b.MoveNext())
                throw new InvalidOperationException($"fast-lookup wide sample drift at tick {tick}.");

            foreach (var direction in new[] { TimelineDirection.Forward, TimelineDirection.Reverse })
            {
                var fa = wideSearched.VisitFrames(in wideTrackSearched, tick, direction);
                var fb = wideFast.VisitFrames(in wideTrackFast, tick, direction);

                while (fa.MoveNext())
                {
                    if (!fb.MoveNext() || fa.Current.DataOffset != fb.Current.DataOffset ||
                        fa.Current.Weight != fb.Current.Weight || fa.Current.Phase != fb.Current.Phase)
                        throw new InvalidOperationException($"fast-lookup wide frame drift at tick {tick}.");
                }

                if (fb.MoveNext())
                    throw new InvalidOperationException($"fast-lookup wide frame drift at tick {tick}.");
            }
        }

        CompareTraverseWide(wideSearched, wideFast, 4, 5);
        CompareTraverseWide(wideSearched, wideFast, 0, 63);
        CompareTraverseWide(wideSearched, wideFast, 100, 5);
    }

    private static void CompareTraverseWide(ClipQuery<BenchClip> searched, FastQuery<BenchClip> fast, long previous, long current)
    {
        var span = new TimelineSpan(default, previous, current);

        TransitionCounter counterSearched = default;
        searched.TraverseTransitions(in span, ref counterSearched);
        TransitionCounter counterFast = default;
        fast.TraverseTransitions(in span, ref counterFast);

        if (counterSearched.Count != counterFast.Count)
            throw new InvalidOperationException(
                $"fast-lookup wide traverse drift for span ({previous}, {current}).");
    }

    private static void CompareTraverse(ClipQuery<BenchClip> searched, FastQuery<BenchClip> fast, long previous, long current)
    {
        var span = new TimelineSpan(default, previous, current);

        TransitionCounter counterSearched = default;
        var emittedSearched = searched.TraverseTransitions(in span, ref counterSearched);
        TransitionCounter counterFast = default;
        var emittedFast = fast.TraverseTransitions(in span, ref counterFast);

        if (emittedSearched != emittedFast || counterSearched.Count != counterFast.Count)
            throw new InvalidOperationException(
                $"fast-lookup traverse drift for span ({previous}, {current}).");
    }

    [Benchmark]
    public float FastFrameExclusive()
    {
        var view = _dbExclusive.AsView();
        var query = view.Query(_handleExclusive);
        var track = query.Tracks(_timelines[0])[0];
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
    public float FastSampleCrossFade()
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
    public float FastSampleFusedOneTrack()
    {
        var view = _dbExclusive.AsView();
        var query = view.Query(_handleExclusive);
        var track = query.Tracks(_timelines[0])[0];
        SampleSum acc = default;
        query.Sample(in track, 24, ref acc);
        return acc.A + acc.B;
    }

    [Benchmark]
    public float FastSampleEightCursors()
    {
        var view = _dbExclusive.AsView();
        var query = view.Query(_handleExclusive);
        SampleSum sum = default;
        query.Sample(_eightCursors.AsSpan(), ref sum);
        return sum.A + sum.B;
    }

    [Benchmark]
    public float FastVisitOneCursor()
    {
        var view = _dbExclusive.AsView();
        var query = view.Query(_handleExclusive);
        WeightedSum sum = default;
        query.VisitFrames(_singleCursor.AsSpan(), ref sum);
        return sum.A + sum.B;
    }

    [Benchmark]
    public long FastTraverseForwardOneTick()
    {
        var view = _dbPulse.AsView();
        var query = view.Query(_handlePulse);
        TimelineSpan span = new(_pulseTimeline, 4, 5);
        TransitionCounter counter = default;
        return query.TraverseTransitions(in span, ref counter) + counter.Count;
    }

    [Benchmark]
    public long FastTraverseForwardFullLoop()
    {
        var view = _dbPulse.AsView();
        var query = view.Query(_handlePulse);
        TimelineSpan span = new(_pulseTimeline, 0, 63);
        TransitionCounter counter = default;
        return query.TraverseTransitions(in span, ref counter) + counter.Count;
    }

    [Benchmark]
    public long FastTraverseRewindTwentyFive()
    {
        var view = _dbPulse.AsView();
        var query = view.Query(_handlePulse);
        TimelineSpan span = new(_pulseTimeline, 25, 5);
        TransitionCounter counter = default;
        return query.TraverseTransitions(in span, ref counter) + counter.Count;
    }

    [Benchmark]
    public float FastWideSampleFusedOneTrack()
    {
        var view = _dbWide.AsView();
        var query = view.Query(_handleWide);
        var track = query.Tracks(_wideTimeline)[0];
        SampleSum acc = default;
        query.Sample(in track, 100, ref acc);
        return acc.A + acc.B;
    }

    [Benchmark]
    public long FastWideTraverseForwardOneTick()
    {
        var view = _dbWide.AsView();
        var query = view.Query(_handleWide);
        TimelineSpan span = new(_wideTimeline, 4, 5);
        TransitionCounter counter = default;
        return query.TraverseTransitions(in span, ref counter) + counter.Count;
    }
}
