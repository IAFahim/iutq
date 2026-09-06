using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Iutq.Core.Primitives;
using Iutq.Core.Querying;

namespace Iutq.Core.Storage;

/// <summary>
///     Fast-lookup spans carved from the optional blob fast section. Empty
///     (Present false) for flag-free v2 blobs.
/// </summary>
internal readonly ref struct FastLookupSections
{
    public readonly bool Present;
    public readonly ReadOnlySpan<TrackFastData> Descriptors;
    public readonly ReadOnlySpan<byte> Lut8;
    public readonly ReadOnlySpan<ushort> Lut16;
    public readonly ReadOnlySpan<ushort> Prefix;
    public readonly ReadOnlySpan<float> BlendFactors;

    public FastLookupSections(
        bool present,
        ReadOnlySpan<TrackFastData> descriptors,
        ReadOnlySpan<byte> lut8,
        ReadOnlySpan<ushort> lut16,
        ReadOnlySpan<ushort> prefix,
        ReadOnlySpan<float> blendFactors)
    {
        Present = present;
        Descriptors = descriptors;
        Lut8 = lut8;
        Lut16 = lut16;
        Prefix = prefix;
        BlendFactors = blendFactors;
    }
}

/// <summary>
///     View over a database baked with the fast-lookup section
///     (<c>Build(true)</c>). Keeps the plain <see cref="DatabaseView" /> at
///     its original size, so searched-path consumers never pay a byte for the
///     feature; fast-path consumers opt in by binding through this type
///     instead. Query methods fall back to the searched kernel for any track
///     or tick the baked structures do not cover, and results are bit-identical.
/// </summary>
public readonly ref struct FastDatabaseView
{
    /// <summary>The plain view — spans, lookups, payload reads.</summary>
    public readonly DatabaseView View;

    internal readonly FastLookupSections Fast;

    internal FastDatabaseView(DatabaseView view, FastLookupSections fast)
    {
        View = view;
        Fast = fast;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastQuery<TClip> Query<TClip>(ClipTypeHandle<TClip> handle) where TClip : unmanaged
    {
        return new FastQuery<TClip>(View.Query(handle), Fast);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FastQuery<TClip> Query<TClip>(ClipType<TClip> type) where TClip : unmanaged
    {
        return new FastQuery<TClip>(View.Query(type), Fast);
    }
}

/// <summary>
///     Query kernel backed by the baked tick-to-clip LUTs, blend factors and
///     boundary prefix tables. Semantics mirror <see cref="ClipQuery{TClip}" />
///     bit for bit; uncovered ticks and templates fall through to the same
///     searched code.
/// </summary>
public readonly ref struct FastQuery<TClip>
    where TClip : unmanaged
{
    private readonly ClipQuery<TClip> _query;
    private readonly FastLookupSections _fast;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal FastQuery(ClipQuery<TClip> query, FastLookupSections fast)
    {
        _query = query;
        _fast = fast;
    }

    public ReadOnlySpan<TrackInstance> Tracks(TimelineIndex timelineIndex) => _query.Tracks(timelineIndex);

    public ref readonly TrackTemplate TrackTemplate(in TrackInstance track) => ref _query.TrackTemplate(in track);

    public TrackRef Track(in TrackInstance track) => _query.Track(in track);

    public ref readonly TClip Data(int dataOffset) => ref _query.Data(dataOffset);

    public ref readonly TClip Data(in ClipSample sample) => ref _query.Data(in sample);

    public ref readonly TClip Data(in ClipFrame frame) => ref _query.Data(in frame);

    public ref readonly TClip Data(in ClipTransition transition) => ref _query.Data(in transition);

    // ---- Sampling ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ClipSampleEnumerator Sample(in TrackInstance track, int tick)
    {
        if (_fast.Present)
        {
            ref readonly var fast = ref _fast.Descriptors[track.TrackTemplateId];

            if (fast.LutWidth != 0 && (uint)tick < (uint)fast.LutCount)
            {
                var (indexA, indexB, factor) = FastActive(in fast, tick);
                ref readonly var data = ref _query.TrackTemplate(in track);

                return data.Mode == TrackMode.Exclusive
                    ? SampleExclusiveAt(in data, indexA)
                    : SampleCrossFadeAt(in data, indexA, indexB, factor);
            }
        }

        return _query.Sample(in track, tick);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Sample<TVisitor>(
        in TrackInstance track,
        int tick,
        ref TVisitor visitor)
        where TVisitor : struct, IClipSampleVisitor<TClip>
    {
        var db = _query.Database;
        var samples = Sample(in track, tick);

        while (samples.MoveNext())
        {
            var sample = samples.Current;
            visitor.Sample(in track, in db.Payload<TClip>(sample.DataOffset), sample.Weight);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Sample<TVisitor>(
        ReadOnlySpan<TimelineCursor> timelines,
        ref TVisitor visitor)
        where TVisitor : struct, IClipSampleVisitor<TClip>
    {
        foreach (ref readonly var cursor in timelines)
        foreach (ref readonly var track in Tracks(cursor.Timeline))
        {
            Sample(in track, cursor.Tick, ref visitor);
        }
    }

    // ---- Frames ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ClipFrameEnumerator VisitFrames(
        in TrackInstance track,
        in TimelineCursor cursor)
    {
        return VisitFrames(in track, cursor.Tick, cursor.Direction);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ClipFrameEnumerator VisitFrames(
        in TrackInstance track,
        int tick,
        TimelineDirection direction)
    {
        if (_fast.Present)
        {
            ref readonly var fast = ref _fast.Descriptors[track.TrackTemplateId];

            if (fast.LutWidth != 0 && (uint)tick < (uint)fast.LutCount)
            {
                var (indexA, indexB, factor) = FastActive(in fast, tick);
                ref readonly var data = ref _query.TrackTemplate(in track);

                return data.Mode == TrackMode.Exclusive
                    ? FrameExclusiveAt(in data, indexA, tick, direction)
                    : FrameCrossFadeAt(in data, indexA, indexB, tick, direction, factor);
            }
        }

        return _query.VisitFrames(in track, tick, direction);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void VisitFrames<TVisitor>(
        in TrackInstance track,
        int tick,
        TimelineDirection direction,
        ref TVisitor visitor)
        where TVisitor : struct, IClipFrameVisitor<TClip>
    {
        var db = _query.Database;
        var frames = VisitFrames(in track, tick, direction);

        while (frames.MoveNext())
        {
            var frame = frames.Current;
            visitor.Visit(in track, in frame, in db.Payload<TClip>(frame.DataOffset));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void VisitFrames<TVisitor>(
        ReadOnlySpan<TimelineCursor> timelines,
        ref TVisitor visitor)
        where TVisitor : struct, IClipFrameVisitor<TClip>
    {
        foreach (ref readonly var cursor in timelines)
        foreach (ref readonly var track in Tracks(cursor.Timeline))
        {
            VisitFrames(in track, cursor.Tick, cursor.Direction, ref visitor);
        }
    }

    // ---- Traversal ----

    public long TraverseTransitions<TVisitor>(
        in TimelineSpan span,
        ref TVisitor visitor)
        where TVisitor : struct, IClipTransitionVisitor<TClip>
    {
        var db = _query.Database;

        if (span.CurrentRawTick == span.PreviousRawTick || !db.TryGetTimeline(span.Timeline, out var timeline))
            return 0;

        var tracks = Tracks(span.Timeline);

        if (tracks.IsEmpty) return 0;

        return span.CurrentRawTick > span.PreviousRawTick
            ? TraverseForward(db, tracks, in timeline, span.PreviousRawTick, span.CurrentRawTick, ref visitor)
            : TraverseReverse(db, tracks, in timeline, span.PreviousRawTick, span.CurrentRawTick, ref visitor);
    }

    // ---- Lambda forms (mirror ClipQuery) ----

    public delegate void SampleAction<TState>(ref TState state, in TrackInstance track, in TClip clip, float weight);

    public delegate void FrameAction<TState>(ref TState state, in TrackInstance track, in ClipFrame frame, in TClip clip);

    public delegate void ClipTransitionAction<TState>(
        ref TState state,
        in TrackInstance track,
        in ClipTransition transition,
        in TClip clip);

    public delegate void BlendTransitionAction<TState>(
        ref TState state,
        in TrackInstance track,
        in BlendTransition transition,
        in TClip clipA,
        in TClip clipB);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Sample<TState>(
        in TrackInstance track,
        int tick,
        ref TState state,
        SampleAction<TState> action)
    {
        var db = _query.Database;
        var samples = Sample(in track, tick);

        while (samples.MoveNext())
        {
            var sample = samples.Current;
            action(ref state, in track, in db.Payload<TClip>(sample.DataOffset), sample.Weight);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Sample<TState>(
        ReadOnlySpan<TimelineCursor> timelines,
        ref TState state,
        SampleAction<TState> action)
    {
        foreach (ref readonly var cursor in timelines)
        foreach (ref readonly var track in Tracks(cursor.Timeline))
        {
            Sample(in track, cursor.Tick, ref state, action);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void VisitFrames<TState>(
        in TrackInstance track,
        int tick,
        TimelineDirection direction,
        ref TState state,
        FrameAction<TState> action)
    {
        var db = _query.Database;
        var frames = VisitFrames(in track, tick, direction);

        while (frames.MoveNext())
        {
            var frame = frames.Current;
            action(ref state, in track, in frame, in db.Payload<TClip>(frame.DataOffset));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void VisitFrames<TState>(
        ReadOnlySpan<TimelineCursor> timelines,
        ref TState state,
        FrameAction<TState> action)
    {
        foreach (ref readonly var cursor in timelines)
        foreach (ref readonly var track in Tracks(cursor.Timeline))
        {
            VisitFrames(in track, cursor.Tick, cursor.Direction, ref state, action);
        }
    }

    public long TraverseTransitions<TState>(
        in TimelineSpan span,
        ref TState state,
        ClipTransitionAction<TState> onClip,
        BlendTransitionAction<TState> onBlend)
    {
        DelegateTransitionVisitor<TState> adapter = new(onClip, onBlend, state);
        var emitted = TraverseTransitions(in span, ref adapter);
        state = adapter.State;
        return emitted;
    }

    private struct DelegateTransitionVisitor<TState> : IClipTransitionVisitor<TClip>
    {
        public TState State;

        private readonly ClipTransitionAction<TState> _onClip;
        private readonly BlendTransitionAction<TState> _onBlend;

        public DelegateTransitionVisitor(
            ClipTransitionAction<TState> onClip,
            BlendTransitionAction<TState> onBlend,
            TState state)
        {
            _onClip = onClip;
            _onBlend = onBlend;
            State = state;
        }

        public void OnClipTransition(in TrackInstance track, in ClipTransition transition, in TClip clip)
        {
            _onClip(ref State, in track, in transition, in clip);
        }

        public void OnBlendTransition(
            in TrackInstance track,
            in BlendTransition transition,
            in TClip clipA,
            in TClip clipB)
        {
            _onBlend(ref State, in track, in transition, in clipA, in clipB);
        }
    }

    // ---- LUT kernels ----

    /// <summary>
    ///     LUT hit: resolves the active clip indices within the template
    ///     window (-1 = none) and the baked blend factor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (int IndexA, int IndexB, float Factor) FastActive(in TrackFastData fast, int tick)
    {
        if (fast.LutWidth == 1)
            return (_fast.Lut8[fast.LutStart + tick] - 1, -1, 0f);

        if (fast.LutWidth == 2)
            return (_fast.Lut16[fast.LutStart + tick] - 1, -1, 0f);

        if (fast.LutWidth == 3)
        {
            var baseIndex = fast.LutStart + tick * 2;
            return (
                _fast.Lut8[baseIndex] - 1,
                _fast.Lut8[baseIndex + 1] - 1,
                _fast.BlendFactors[fast.FactorStart + tick]);
        }

        var offset = fast.LutStart + tick * 2;
        return (
            _fast.Lut16[offset] - 1,
            _fast.Lut16[offset + 1] - 1,
            _fast.BlendFactors[fast.FactorStart + tick]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref readonly ClipEntry ClipAt(in TrackTemplate data, int index)
    {
        ref var first = ref MemoryMarshal.GetReference(_query.Database.Clips);
        return ref Unsafe.Add(ref first, (nint)(data.ClipStart + index));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ClipSampleEnumerator SampleExclusiveAt(in TrackTemplate data, int index)
    {
        if (index < 0) return default;

        ref readonly var clip = ref ClipAt(in data, index);
        return new ClipSampleEnumerator(new ClipSample(clip.DataOffset, 1f));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ClipSampleEnumerator SampleCrossFadeAt(in TrackTemplate data, int indexA, int indexB, float factor)
    {
        if (indexA < 0 && indexB < 0) return default;

        if (indexB < 0)
            return new ClipSampleEnumerator(new ClipSample(ClipAt(in data, indexA).DataOffset, 1f));

        if (indexA < 0)
            return new ClipSampleEnumerator(new ClipSample(ClipAt(in data, indexB).DataOffset, 1f));

        ref readonly var clipA = ref ClipAt(in data, indexA);
        ref readonly var clipB = ref ClipAt(in data, indexB);

        var weightB = clipB.Start >= clipA.Start ? factor : 1f - factor;
        var weightA = 1f - weightB;

        return new ClipSampleEnumerator(
            new ClipSample(clipA.DataOffset, weightA),
            new ClipSample(clipB.DataOffset, weightB));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ClipFrameEnumerator FrameExclusiveAt(
        in TrackTemplate data,
        int index,
        int tick,
        TimelineDirection direction)
    {
        if (index < 0) return default;

        ref readonly var clip = ref ClipAt(in data, index);
        return new ClipFrameEnumerator(new ClipFrame(
            clip.Start,
            clip.End,
            tick,
            clip.DataOffset,
            direction,
            BlendPhase.None,
            clip.Ease,
            1f,
            0f));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ClipFrameEnumerator FrameCrossFadeAt(
        in TrackTemplate data,
        int indexA,
        int indexB,
        int tick,
        TimelineDirection direction,
        float factor)
    {
        if (indexA < 0 && indexB < 0) return default;

        if (indexB < 0) return FrameExclusiveAt(in data, indexA, tick, direction);

        if (indexA < 0) return FrameExclusiveAt(in data, indexB, tick, direction);

        ref readonly var clipA = ref ClipAt(in data, indexA);
        ref readonly var clipB = ref ClipAt(in data, indexB);
        var blendStart = Math.Max(clipA.Start, clipB.Start);
        var blendEnd = Math.Min(clipA.End, clipB.End);
        var blendPhase = TimelineMath.BlendPhaseAt(tick, blendStart, blendEnd, direction);

        var weightB = clipB.Start >= clipA.Start ? factor : 1f - factor;
        var weightA = 1f - weightB;

        var frameA = new ClipFrame(
            clipA.Start,
            clipA.End,
            tick,
            clipA.DataOffset,
            direction,
            blendPhase,
            clipA.Ease,
            weightA,
            factor);
        var frameB = new ClipFrame(
            clipB.Start,
            clipB.End,
            tick,
            clipB.DataOffset,
            direction,
            blendPhase,
            clipB.Ease,
            weightB,
            factor);

        return new ClipFrameEnumerator(in frameA, in frameB);
    }

    // ---- Traversal core (prefix windows over the searched emission) ----

    private long TraverseForward<TVisitor>(
        DatabaseView db,
        ReadOnlySpan<TrackInstance> tracks,
        in TimelineHeader timeline,
        long previousRawTick,
        long currentRawTick,
        ref TVisitor visitor)
        where TVisitor : struct, IClipTransitionVisitor<TClip>
    {
        var firstRaw = previousRawTick + 1;
        var lastRaw = currentRawTick;

        if ((timeline.Flags & TimelineFlags.Loop) == 0)
        {
            var first = Math.Max(firstRaw, 0);
            var last = Math.Min(lastRaw, timeline.Duration - 1L);

            if (first > last) return 0;

            return EmitRange(db, tracks, (int)first, (int)last, 0, timeline.Duration, TimelineDirection.Forward, ref visitor);
        }

        var duration = timeline.Duration;

        if ((ulong)firstRaw < (ulong)duration && (ulong)lastRaw < (ulong)duration)
            return EmitRange(db, tracks, (int)firstRaw, (int)lastRaw, 0, duration, TimelineDirection.Forward, ref visitor);

        var firstCycle = FloorDiv(firstRaw, duration);
        var lastCycle = FloorDiv(lastRaw, duration);

        if (firstCycle == lastCycle)
            return EmitRange(
                db, tracks,
                (int)(firstRaw - firstCycle * duration),
                (int)(lastRaw - lastCycle * duration),
                firstCycle,
                duration,
                TimelineDirection.Forward,
                ref visitor);

        var emitted = EmitRange(
            db, tracks,
            (int)(firstRaw - firstCycle * duration),
            duration - 1,
            firstCycle,
            duration,
            TimelineDirection.Forward,
            ref visitor);

        for (var cycle = firstCycle + 1; cycle < lastCycle; cycle++)
            emitted += EmitRange(db, tracks, 0, duration - 1, cycle, duration, TimelineDirection.Forward, ref visitor);

        return emitted + EmitRange(
            db, tracks,
            0,
            (int)(lastRaw - lastCycle * duration),
            lastCycle,
            duration,
            TimelineDirection.Forward,
            ref visitor);
    }

    private long TraverseReverse<TVisitor>(
        DatabaseView db,
        ReadOnlySpan<TrackInstance> tracks,
        in TimelineHeader timeline,
        long previousRawTick,
        long currentRawTick,
        ref TVisitor visitor)
        where TVisitor : struct, IClipTransitionVisitor<TClip>
    {
        var lowRaw = currentRawTick;
        var highRaw = previousRawTick - 1;

        if ((timeline.Flags & TimelineFlags.Loop) == 0)
        {
            var low = Math.Max(lowRaw, 0);
            var high = Math.Min(highRaw, timeline.Duration - 1L);

            if (low > high) return 0;

            return EmitRange(db, tracks, (int)low, (int)high, 0, timeline.Duration, TimelineDirection.Reverse, ref visitor);
        }

        var duration = timeline.Duration;

        if ((ulong)lowRaw < (ulong)duration && (ulong)highRaw < (ulong)duration)
            return EmitRange(db, tracks, (int)lowRaw, (int)highRaw, 0, duration, TimelineDirection.Reverse, ref visitor);

        var lowCycle = FloorDiv(lowRaw, duration);
        var highCycle = FloorDiv(highRaw, duration);

        if (lowCycle == highCycle)
            return EmitRange(
                db, tracks,
                (int)(lowRaw - lowCycle * duration),
                (int)(highRaw - highCycle * duration),
                lowCycle,
                duration,
                TimelineDirection.Reverse,
                ref visitor);

        var emitted = EmitRange(
            db, tracks,
            0,
            (int)(highRaw - highCycle * duration),
            highCycle,
            duration,
            TimelineDirection.Reverse,
            ref visitor);

        for (var cycle = highCycle - 1; cycle > lowCycle; cycle--)
            emitted += EmitRange(db, tracks, 0, duration - 1, cycle, duration, TimelineDirection.Reverse, ref visitor);

        return emitted + EmitRange(
            db, tracks,
            (int)(lowRaw - lowCycle * duration),
            duration - 1,
            lowCycle,
            duration,
            TimelineDirection.Reverse,
            ref visitor);
    }

    private long EmitRange<TVisitor>(
        DatabaseView db,
        ReadOnlySpan<TrackInstance> tracks,
        int localLow,
        int localHigh,
        long cycle,
        int duration,
        TimelineDirection direction,
        ref TVisitor visitor)
        where TVisitor : struct, IClipTransitionVisitor<TClip>
    {
        long emitted = 0;

        foreach (ref readonly var track in tracks)
        {
            ref readonly var data = ref _query.TrackTemplate(in track);
            ref var boundariesFirst = ref MemoryMarshal.GetReference(db.Boundaries);
            var boundaries = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.Add(ref boundariesFirst, (nint)(uint)data.BoundaryStart),
                data.BoundaryCount);

            if (_fast.Present)
            {
                ref readonly var fast = ref _fast.Descriptors[track.TrackTemplateId];

                if (fast.PrefixCount != 0 && (uint)localHigh + 1 < (uint)fast.PrefixCount)
                {
                    // Prefix table: boundaries[Tick >= localLow] ..
                    // boundaries[Tick > localHigh] without a search.
                    var from = _fast.Prefix[fast.PrefixStart + localLow];
                    var to = _fast.Prefix[fast.PrefixStart + localHigh + 1];

                    if (direction == TimelineDirection.Forward)
                    {
                        for (var index = from; index < to; index++)
                        {
                            EmitBoundary(db, in track, in data, in Unsafe.Add(ref boundariesFirst, (nint)(uint)(data.BoundaryStart + index)), cycle, duration, TimelineDirection.Forward, ref visitor);
                            emitted++;
                        }
                    }
                    else
                    {
                        for (var index = to - 1; index >= from; index--)
                        {
                            EmitBoundary(db, in track, in data, in Unsafe.Add(ref boundariesFirst, (nint)(uint)(data.BoundaryStart + index)), cycle, duration, TimelineDirection.Reverse, ref visitor);
                            emitted++;
                        }
                    }

                    continue;
                }
            }

            emitted += direction == TimelineDirection.Forward
                ? EmitForwardTrack(db, in track, in data, boundaries, localLow, localHigh, cycle, duration, ref visitor)
                : EmitReverseTrack(db, in track, in data, boundaries, localLow, localHigh, cycle, duration, ref visitor);
        }

        return emitted;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long EmitForwardTrack<TVisitor>(
        DatabaseView db,
        in TrackInstance track,
        in TrackTemplate data,
        ReadOnlySpan<TrackBoundary> boundaries,
        int localLow,
        int localHigh,
        long cycle,
        int duration,
        ref TVisitor visitor)
        where TVisitor : struct, IClipTransitionVisitor<TClip>
    {
        ref var first = ref MemoryMarshal.GetReference(boundaries);
        var length = boundaries.Length;
        var index = LowerBound(boundaries, localLow);
        long emitted = 0;

        for (; index < length; index++)
        {
            ref readonly var boundary = ref Unsafe.Add(ref first, (nint)(uint)index);

            if (boundary.Tick > localHigh) break;

            EmitBoundary(db, in track, in data, in boundary, cycle, duration, TimelineDirection.Forward, ref visitor);
            emitted++;
        }

        return emitted;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long EmitReverseTrack<TVisitor>(
        DatabaseView db,
        in TrackInstance track,
        in TrackTemplate data,
        ReadOnlySpan<TrackBoundary> boundaries,
        int localLow,
        int localHigh,
        long cycle,
        int duration,
        ref TVisitor visitor)
        where TVisitor : struct, IClipTransitionVisitor<TClip>
    {
        ref var first = ref MemoryMarshal.GetReference(boundaries);
        var index = UpperBound(boundaries, localHigh) - 1;
        long emitted = 0;

        for (; index >= 0; index--)
        {
            ref readonly var boundary = ref Unsafe.Add(ref first, (nint)(uint)index);

            if (boundary.Tick < localLow) break;

            EmitBoundary(db, in track, in data, in boundary, cycle, duration, TimelineDirection.Reverse, ref visitor);
            emitted++;
        }

        return emitted;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitBoundary<TVisitor>(
        DatabaseView db,
        in TrackInstance track,
        in TrackTemplate data,
        in TrackBoundary boundary,
        long cycle,
        int duration,
        TimelineDirection direction,
        ref TVisitor visitor)
        where TVisitor : struct, IClipTransitionVisitor<TClip>
    {
        var occurrenceTick = checked(cycle * duration + boundary.Tick);
        ref var clipBase = ref MemoryMarshal.GetReference(db.Clips);
        ref readonly var clipA = ref Unsafe.Add(ref clipBase, (nint)(data.ClipStart + boundary.ClipA));

        switch (boundary.Kind)
        {
            case BoundaryKind.ClipInstant:
            {
                ClipTransition transition = new(
                    occurrenceTick,
                    boundary.Tick,
                    direction,
                    ClipPhase.Enter,
                    clipA.DataOffset);
                visitor.OnClipTransition(in track, in transition, in db.Payload<TClip>(clipA.DataOffset));
                return;
            }

            case BoundaryKind.ClipStart:
            case BoundaryKind.ClipEnd:
            {
                var phase = direction == TimelineDirection.Forward
                    ? boundary.Kind == BoundaryKind.ClipStart ? ClipPhase.Enter : ClipPhase.Exit
                    : boundary.Kind == BoundaryKind.ClipEnd
                        ? ClipPhase.Enter
                        : ClipPhase.Exit;

                ClipTransition transition = new(
                    occurrenceTick,
                    boundary.Tick,
                    direction,
                    phase,
                    clipA.DataOffset);
                visitor.OnClipTransition(in track, in transition, in db.Payload<TClip>(clipA.DataOffset));
                return;
            }

            default: // BlendStart (1) / BlendInstant (3) / BlendEnd (4)
            {
                ref readonly var clipB = ref Unsafe.Add(ref clipBase, (nint)(data.ClipStart + boundary.ClipB));
                var start = Math.Max(clipA.Start, clipB.Start);
                var end = Math.Min(clipA.End, clipB.End);
                BlendPhase phase;

                if (boundary.Kind == BoundaryKind.BlendInstant)
                    phase = BlendPhase.Enter;
                else if (direction == TimelineDirection.Forward)
                    phase = boundary.Kind == BoundaryKind.BlendStart
                        ? BlendPhase.Enter
                        : BlendPhase.Exit;
                else
                    phase = boundary.Kind == BoundaryKind.BlendEnd
                        ? BlendPhase.Enter
                        : BlendPhase.Exit;

                BlendTransition transition = new(
                    occurrenceTick,
                    boundary.Tick,
                    direction,
                    phase,
                    clipA.DataOffset,
                    clipB.DataOffset,
                    TimelineMath.BlendFactor(boundary.Tick, start, end));

                visitor.OnBlendTransition(
                    in track,
                    in transition,
                    in db.Payload<TClip>(clipA.DataOffset),
                    in db.Payload<TClip>(clipB.DataOffset));
                return;
            }
        }
    }

    private const int LinearBoundaryScanThreshold = 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LowerBound(ReadOnlySpan<TrackBoundary> boundaries, int tick)
    {
        ref var first = ref MemoryMarshal.GetReference(boundaries);
        var length = boundaries.Length;

        if (length <= LinearBoundaryScanThreshold)
        {
            for (var i = 0; i < length; i++)
                if (Unsafe.Add(ref first, (nint)(uint)i).Tick >= tick)
                    return i;

            return length;
        }

        var lo = 0;
        var hi = length;

        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;

            if (Unsafe.Add(ref first, (nint)(uint)mid).Tick < tick)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int UpperBound(ReadOnlySpan<TrackBoundary> boundaries, int tick)
    {
        ref var first = ref MemoryMarshal.GetReference(boundaries);
        var length = boundaries.Length;

        if (length <= LinearBoundaryScanThreshold)
        {
            for (var i = length - 1; i >= 0; i--)
                if (Unsafe.Add(ref first, (nint)(uint)i).Tick <= tick)
                    return i + 1;

            return 0;
        }

        var lo = 0;
        var hi = length;

        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;

            if (Unsafe.Add(ref first, (nint)(uint)mid).Tick <= tick)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long FloorDiv(long value, int divisor)
    {
        if (value >= int.MinValue && value <= int.MaxValue)
        {
            var i = (int)value;
            var quotient = i / divisor;

            return i >= 0 || quotient * divisor == i
                ? quotient
                : quotient - 1;
        }

        var wideQuotient = value / divisor;

        return value >= 0 || wideQuotient * divisor == value
            ? wideQuotient
            : wideQuotient - 1;
    }
}
