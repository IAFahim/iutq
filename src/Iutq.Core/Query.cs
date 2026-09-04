using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Iutq.Core;

public readonly ref struct ClipQuery<TClip>
    where TClip : unmanaged
{
    private readonly DatabaseView _db;
    private readonly int _directoryBase;

    internal ClipQuery(DatabaseView db, int typeSlot)
    {
        _db = db;
        _directoryBase = checked(typeSlot * db.Timelines.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<TrackInstance> Tracks(TimelineId timelineId)
    {
        if ((uint)timelineId.Value >= (uint)_db.Timelines.Length)
        {
            return ReadOnlySpan<TrackInstance>.Empty;
        }

        TypePartition partition = _db.Directory[_directoryBase + timelineId.Value];
        return _db.Tracks.Slice(partition.TrackStart, partition.TrackCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly TrackData TrackData(in TrackInstance track) =>
        ref _db.TrackData[track.TrackDataId];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TrackRef Track(in TrackInstance track) =>
        new(in track, in _db.TrackData[track.TrackDataId]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly TClip Data(int dataOffset) =>
        ref _db.Payload<TClip>(dataOffset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly TClip Data(in ClipSample sample) =>
        ref _db.Payload<TClip>(sample.DataOffset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly TClip Data(in ClipHit hit) =>
        ref _db.Payload<TClip>(hit.DataOffset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly TClip Data(in ClipTransition transition) =>
        ref _db.Payload<TClip>(transition.DataOffset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ClipSampleEnumerator Sample(in TrackInstance track, int tick)
    {
        ref readonly TrackData data = ref _db.TrackData[track.TrackDataId];

        return data.Mode == TrackMode.Exclusive
            ? SampleExclusive(in data, tick)
            : SampleCrossFade(in data, tick);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Sample<TVisitor>(
        in TrackInstance track,
        int tick,
        ref TVisitor visitor)
        where TVisitor : struct, IClipSampleVisitor<TClip>
    {
        ClipSampleEnumerator samples = Sample(in track, tick);

        while (samples.MoveNext())
        {
            ClipSample sample = samples.Current;
            ref readonly TClip clip = ref _db.Payload<TClip>(sample.DataOffset);
            visitor.Sample(in track, in clip, sample.Weight);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Sample<TVisitor>(
        ReadOnlySpan<TimelineCursor> timelines,
        ref TVisitor visitor)
        where TVisitor : struct, IClipSampleVisitor<TClip>
    {
        foreach (ref readonly TimelineCursor cursor in timelines)
        {
            ReadOnlySpan<TrackInstance> tracks = Tracks(cursor.Timeline);

            foreach (ref readonly TrackInstance track in tracks)
            {
                ClipSampleEnumerator samples = Sample(in track, cursor.Tick);

                while (samples.MoveNext())
                {
                    ClipSample sample = samples.Current;
                    ref readonly TClip clip = ref _db.Payload<TClip>(sample.DataOffset);
                    visitor.Sample(in track, in clip, sample.Weight);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ClipFrameEnumerator Frame(
        in TrackInstance track,
        in TimelineCursor cursor) =>
        Frame(in track, cursor.Tick, cursor.Direction);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ClipFrameEnumerator Frame(
        in TrackInstance track,
        int tick,
        TimelineDirection direction)
    {
        ref readonly TrackData data = ref _db.TrackData[track.TrackDataId];

        return data.Mode == TrackMode.Exclusive
            ? FrameExclusive(in data, tick, direction)
            : FrameCrossFade(in data, tick, direction);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Frame<TVisitor>(
        in TrackInstance track,
        int tick,
        TimelineDirection direction,
        ref TVisitor visitor)
        where TVisitor : struct, IClipFrameVisitor<TClip>
    {
        ClipFrameEnumerator frame = Frame(in track, tick, direction);

        while (frame.MoveNext())
        {
            ClipHit hit = frame.Current;
            ref readonly TClip clip = ref _db.Payload<TClip>(hit.DataOffset);
            visitor.Visit(in track, in hit, in clip);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Visit<TVisitor>(
        ReadOnlySpan<TimelineCursor> timelines,
        ref TVisitor visitor)
        where TVisitor : struct, IClipFrameVisitor<TClip>
    {
        foreach (ref readonly TimelineCursor cursor in timelines)
        {
            ReadOnlySpan<TrackInstance> tracks = Tracks(cursor.Timeline);

            foreach (ref readonly TrackInstance track in tracks)
            {
                ClipFrameEnumerator frame = Frame(in track, in cursor);

                while (frame.MoveNext())
                {
                    ClipHit hit = frame.Current;
                    ref readonly TClip clip = ref _db.Payload<TClip>(hit.DataOffset);
                    visitor.Visit(in track, in hit, in clip);
                }
            }
        }
    }

    public long TraverseTransitions<TVisitor>(
        in TimelineSpan span,
        ref TVisitor visitor)
        where TVisitor : struct, IClipTransitionVisitor<TClip>
    {
        if (span.CurrentRawTick == span.PreviousRawTick ||
            !_db.TryGetTimeline(span.Timeline, out TimelineHeader timeline))
        {
            return 0;
        }

        ReadOnlySpan<TrackInstance> tracks = Tracks(span.Timeline);

        if (tracks.IsEmpty)
        {
            return 0;
        }

        return span.CurrentRawTick > span.PreviousRawTick
            ? TraverseForward(tracks, in timeline, span.PreviousRawTick, span.CurrentRawTick, ref visitor)
            : TraverseReverse(tracks, in timeline, span.PreviousRawTick, span.CurrentRawTick, ref visitor);
    }

    public long TraverseTransitions<TVisitor>(
        in TrackInstance track,
        in TimelineSpan span,
        ref TVisitor visitor)
        where TVisitor : struct, IClipTransitionVisitor<TClip>
    {
        if (span.CurrentRawTick == span.PreviousRawTick ||
            !_db.TryGetTimeline(span.Timeline, out TimelineHeader timeline))
        {
            return 0;
        }

        ReadOnlySpan<TrackInstance> single = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in track), 1);

        return span.CurrentRawTick > span.PreviousRawTick
            ? TraverseForward(single, in timeline, span.PreviousRawTick, span.CurrentRawTick, ref visitor)
            : TraverseReverse(single, in timeline, span.PreviousRawTick, span.CurrentRawTick, ref visitor);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ClipSampleEnumerator SampleExclusive(in TrackData data, int tick)
    {
        ReadOnlySpan<ClipHeader> clips = _db.Clips.Slice(data.ClipStart, data.ClipCount);
        int index = ActiveClipIndex(clips, tick);

        if (index < 0)
        {
            return default;
        }

        return new ClipSampleEnumerator(new ClipSample(clips[index].DataOffset, 1f));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ClipSampleEnumerator SampleCrossFade(in TrackData data, int tick)
    {
        ReadOnlySpan<ClipHeader> laneA = _db.Clips.Slice(data.ClipStart, data.LaneSplit);
        ReadOnlySpan<ClipHeader> laneB = _db.Clips.Slice(
            data.ClipStart + data.LaneSplit,
            data.ClipCount - data.LaneSplit);

        int indexA = ActiveClipIndex(laneA, tick);
        int indexB = ActiveClipIndex(laneB, tick);

        if (indexA < 0 && indexB < 0)
        {
            return default;
        }

        if (indexB < 0)
        {
            return new ClipSampleEnumerator(new ClipSample(laneA[indexA].DataOffset, 1f));
        }

        if (indexA < 0)
        {
            return new ClipSampleEnumerator(new ClipSample(laneB[indexB].DataOffset, 1f));
        }

        ref readonly ClipHeader clipA = ref laneA[indexA];
        ref readonly ClipHeader clipB = ref laneB[indexB];
        int blendStart = Math.Max(clipA.Start, clipB.Start);
        int blendEnd = Math.Min(clipA.End, clipB.End);
        float factor = TimelineMath.BlendFactor(tick, blendStart, blendEnd);

        float weightB = clipB.Start >= clipA.Start ? factor : 1f - factor;
        float weightA = 1f - weightB;

        return new ClipSampleEnumerator(
            new ClipSample(clipA.DataOffset, weightA),
            new ClipSample(clipB.DataOffset, weightB));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ClipFrameEnumerator FrameExclusive(
        in TrackData data,
        int tick,
        TimelineDirection direction)
    {
        ReadOnlySpan<ClipHeader> clips = _db.Clips.Slice(data.ClipStart, data.ClipCount);
        int index = ActiveClipIndex(clips, tick);

        if (index < 0)
        {
            return default;
        }

        ref readonly ClipHeader clip = ref clips[index];
        ClipHit hit = CreateHit(
            in clip,
            tick,
            direction,
            BlendPhase.None,
            1f,
            0f);

        return new ClipFrameEnumerator(in hit);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ClipFrameEnumerator FrameCrossFade(
        in TrackData data,
        int tick,
        TimelineDirection direction)
    {
        ReadOnlySpan<ClipHeader> laneA = _db.Clips.Slice(data.ClipStart, data.LaneSplit);
        ReadOnlySpan<ClipHeader> laneB = _db.Clips.Slice(
            data.ClipStart + data.LaneSplit,
            data.ClipCount - data.LaneSplit);

        int indexA = ActiveClipIndex(laneA, tick);
        int indexB = ActiveClipIndex(laneB, tick);

        if (indexA < 0 && indexB < 0)
        {
            return default;
        }

        if (indexB < 0)
        {
            ref readonly ClipHeader clip = ref laneA[indexA];
            ClipHit hit = CreateHit(
                in clip,
                tick,
                direction,
                BlendPhase.None,
                1f,
                0f);
            return new ClipFrameEnumerator(in hit);
        }

        if (indexA < 0)
        {
            ref readonly ClipHeader clip = ref laneB[indexB];
            ClipHit hit = CreateHit(
                in clip,
                tick,
                direction,
                BlendPhase.None,
                1f,
                0f);
            return new ClipFrameEnumerator(in hit);
        }

        ref readonly ClipHeader clipA = ref laneA[indexA];
        ref readonly ClipHeader clipB = ref laneB[indexB];
        int blendStart = Math.Max(clipA.Start, clipB.Start);
        int blendEnd = Math.Min(clipA.End, clipB.End);
        float factor = TimelineMath.BlendFactor(tick, blendStart, blendEnd);
        BlendPhase blendPhase = TimelineMath.BlendPhaseAt(
            tick,
            blendStart,
            blendEnd,
            direction);

        float weightB = clipB.Start >= clipA.Start ? factor : 1f - factor;
        float weightA = 1f - weightB;

        ClipHit hitA = CreateHit(
            in clipA,
            tick,
            direction,
            blendPhase,
            weightA,
            factor);
        ClipHit hitB = CreateHit(
            in clipB,
            tick,
            direction,
            blendPhase,
            weightB,
            factor);

        return new ClipFrameEnumerator(in hitA, in hitB);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ClipHit CreateHit(
        in ClipHeader clip,
        int tick,
        TimelineDirection direction,
        BlendPhase blendPhase,
        float weight,
        float blendFactor) =>
        new(
            clip.Start,
            clip.End,
            tick,
            clip.DataOffset,
            direction,
            blendPhase,
            clip.Ease,
            weight,
            blendFactor);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ActiveClipIndex(ReadOnlySpan<ClipHeader> clips, int tick)
    {
        int lo = 0;
        int hi = clips.Length - 1;

        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            ref readonly ClipHeader clip = ref clips[mid];

            if (tick < clip.Start)
            {
                hi = mid - 1;
            }
            else if (tick >= clip.End)
            {
                lo = mid + 1;
            }
            else
            {
                return mid;
            }
        }

        return -1;
    }

    private long TraverseForward<TVisitor>(
        ReadOnlySpan<TrackInstance> tracks,
        in TimelineHeader timeline,
        long previousRawTick,
        long currentRawTick,
        ref TVisitor visitor)
        where TVisitor : struct, IClipTransitionVisitor<TClip>
    {
        long firstRaw = previousRawTick + 1;
        long lastRaw = currentRawTick;

        if ((timeline.Flags & TimelineFlags.Loop) == 0)
        {
            long first = Math.Max(firstRaw, 0);
            long last = Math.Min(lastRaw, timeline.Duration - 1L);

            if (first > last)
            {
                return 0;
            }

            return EmitRange(
                tracks,
                (int)first,
                (int)last,
                0,
                timeline.Duration,
                TimelineDirection.Forward,
                ref visitor);
        }

        long emitted = 0;
        long firstCycle = FloorDiv(firstRaw, timeline.Duration);
        long lastCycle = FloorDiv(lastRaw, timeline.Duration);

        for (long cycle = firstCycle;; cycle++)
        {
            int localStart = cycle == firstCycle ? FloorMod(firstRaw, timeline.Duration) : 0;
            int localEnd = cycle == lastCycle ? FloorMod(lastRaw, timeline.Duration) : timeline.Duration - 1;

            emitted += EmitRange(
                tracks,
                localStart,
                localEnd,
                cycle,
                timeline.Duration,
                TimelineDirection.Forward,
                ref visitor);

            if (cycle == lastCycle)
            {
                break;
            }
        }

        return emitted;
    }

    private long TraverseReverse<TVisitor>(
        ReadOnlySpan<TrackInstance> tracks,
        in TimelineHeader timeline,
        long previousRawTick,
        long currentRawTick,
        ref TVisitor visitor)
        where TVisitor : struct, IClipTransitionVisitor<TClip>
    {
        long lowRaw = currentRawTick;
        long highRaw = previousRawTick - 1;

        if ((timeline.Flags & TimelineFlags.Loop) == 0)
        {
            long low = Math.Max(lowRaw, 0);
            long high = Math.Min(highRaw, timeline.Duration - 1L);

            if (low > high)
            {
                return 0;
            }

            return EmitRange(
                tracks,
                (int)low,
                (int)high,
                0,
                timeline.Duration,
                TimelineDirection.Reverse,
                ref visitor);
        }

        long emitted = 0;
        long lowCycle = FloorDiv(lowRaw, timeline.Duration);
        long highCycle = FloorDiv(highRaw, timeline.Duration);

        for (long cycle = highCycle;; cycle--)
        {
            int localLow = cycle == lowCycle ? FloorMod(lowRaw, timeline.Duration) : 0;
            int localHigh = cycle == highCycle ? FloorMod(highRaw, timeline.Duration) : timeline.Duration - 1;

            emitted += EmitRange(
                tracks,
                localLow,
                localHigh,
                cycle,
                timeline.Duration,
                TimelineDirection.Reverse,
                ref visitor);

            if (cycle == lowCycle)
            {
                break;
            }
        }

        return emitted;
    }

    private long EmitRange<TVisitor>(
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

        foreach (ref readonly TrackInstance track in tracks)
        {
            ref readonly TrackData data = ref _db.TrackData[track.TrackDataId];
            ReadOnlySpan<BoundaryHeader> boundaries = _db.Boundaries.Slice(
                data.BoundaryStart,
                data.BoundaryCount);

            emitted += direction == TimelineDirection.Forward
                ? EmitForwardTrack(in track, in data, boundaries, localLow, localHigh, cycle, duration, ref visitor)
                : EmitReverseTrack(in track, in data, boundaries, localLow, localHigh, cycle, duration, ref visitor);
        }

        return emitted;
    }

    private long EmitForwardTrack<TVisitor>(
        in TrackInstance track,
        in TrackData data,
        ReadOnlySpan<BoundaryHeader> boundaries,
        int localLow,
        int localHigh,
        long cycle,
        int duration,
        ref TVisitor visitor)
        where TVisitor : struct, IClipTransitionVisitor<TClip>
    {
        int index = LowerBound(boundaries, localLow);
        long emitted = 0;

        for (; index < boundaries.Length; index++)
        {
            ref readonly BoundaryHeader boundary = ref boundaries[index];

            if (boundary.Tick > localHigh)
            {
                break;
            }

            EmitBoundary(in track, in data, in boundary, cycle, duration, TimelineDirection.Forward, ref visitor);
            emitted++;
        }

        return emitted;
    }

    private long EmitReverseTrack<TVisitor>(
        in TrackInstance track,
        in TrackData data,
        ReadOnlySpan<BoundaryHeader> boundaries,
        int localLow,
        int localHigh,
        long cycle,
        int duration,
        ref TVisitor visitor)
        where TVisitor : struct, IClipTransitionVisitor<TClip>
    {
        int index = UpperBound(boundaries, localHigh) - 1;
        long emitted = 0;

        for (; index >= 0; index--)
        {
            ref readonly BoundaryHeader boundary = ref boundaries[index];

            if (boundary.Tick < localLow)
            {
                break;
            }

            EmitBoundary(in track, in data, in boundary, cycle, duration, TimelineDirection.Reverse, ref visitor);
            emitted++;
        }

        return emitted;
    }

    private void EmitBoundary<TVisitor>(
        in TrackInstance track,
        in TrackData data,
        in BoundaryHeader boundary,
        long cycle,
        int duration,
        TimelineDirection direction,
        ref TVisitor visitor)
        where TVisitor : struct, IClipTransitionVisitor<TClip>
    {
        long occurrenceTick = checked(cycle * duration + boundary.Tick);
        ref readonly ClipHeader clipA = ref _db.Clips[data.ClipStart + boundary.ClipA];

        switch (boundary.Kind)
        {
            case BoundaryKind.ClipSingle:
            {
                ClipTransition transition = new(
                    occurrenceTick,
                    boundary.Tick,
                    direction,
                    ClipPhase.Enter,
                    clipA.DataOffset);
                ref readonly TClip payload = ref _db.Payload<TClip>(clipA.DataOffset);
                visitor.Clip(in track, in transition, in payload);
                return;
            }

            case BoundaryKind.ClipLeft:
            case BoundaryKind.ClipRight:
            {
                ClipPhase phase = direction == TimelineDirection.Forward
                    ? boundary.Kind == BoundaryKind.ClipLeft ? ClipPhase.Enter : ClipPhase.Exit
                    : boundary.Kind == BoundaryKind.ClipRight ? ClipPhase.Enter : ClipPhase.Exit;

                ClipTransition transition = new(
                    occurrenceTick,
                    boundary.Tick,
                    direction,
                    phase,
                    clipA.DataOffset);
                ref readonly TClip payload = ref _db.Payload<TClip>(clipA.DataOffset);
                visitor.Clip(in track, in transition, in payload);
                return;
            }

            case BoundaryKind.BlendSingle:
            case BoundaryKind.BlendLeft:
            case BoundaryKind.BlendRight:
            {
                ref readonly ClipHeader clipB = ref _db.Clips[data.ClipStart + boundary.ClipB];
                int start = Math.Max(clipA.Start, clipB.Start);
                int end = Math.Min(clipA.End, clipB.End);
                BlendPhase phase;

                if (boundary.Kind == BoundaryKind.BlendSingle)
                {
                    phase = BlendPhase.Enter;
                }
                else if (direction == TimelineDirection.Forward)
                {
                    phase = boundary.Kind == BoundaryKind.BlendLeft
                        ? BlendPhase.Enter
                        : BlendPhase.Exit;
                }
                else
                {
                    phase = boundary.Kind == BoundaryKind.BlendRight
                        ? BlendPhase.Enter
                        : BlendPhase.Exit;
                }

                BlendTransition transition = new(
                    occurrenceTick,
                    boundary.Tick,
                    direction,
                    phase,
                    clipA.DataOffset,
                    clipB.DataOffset,
                    TimelineMath.BlendFactor(boundary.Tick, start, end));

                ref readonly TClip payloadA = ref _db.Payload<TClip>(clipA.DataOffset);
                ref readonly TClip payloadB = ref _db.Payload<TClip>(clipB.DataOffset);
                visitor.Blend(in track, in transition, in payloadA, in payloadB);
                return;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LowerBound(ReadOnlySpan<BoundaryHeader> boundaries, int tick)
    {
        int lo = 0;
        int hi = boundaries.Length;

        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;

            if (boundaries[mid].Tick < tick)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int UpperBound(ReadOnlySpan<BoundaryHeader> boundaries, int tick)
    {
        int lo = 0;
        int hi = boundaries.Length;

        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;

            if (boundaries[mid].Tick <= tick)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long FloorDiv(long value, int divisor)
    {
        long quotient = value / divisor;
        long remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorMod(long value, int divisor)
    {
        long remainder = value % divisor;
        return (int)(remainder < 0 ? remainder + divisor : remainder);
    }
}

public ref struct ClipSampleEnumerator
{
    private readonly ClipSample _first;
    private readonly ClipSample _second;
    private readonly byte _count;
    private int _index;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ClipSampleEnumerator(scoped in ClipSample first)
    {
        _first = first;
        _second = default;
        _count = 1;
        _index = -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ClipSampleEnumerator(scoped in ClipSample first, scoped in ClipSample second)
    {
        _first = first;
        _second = second;
        _count = 2;
        _index = -1;
    }

    public readonly ClipSample Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _index == 0 ? _first : _second;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        int next = _index + 1;

        if ((uint)next >= _count)
        {
            return false;
        }

        _index = next;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ClipSampleEnumerator GetEnumerator() => this;
}

public ref struct ClipFrameEnumerator
{
    private readonly ClipHit _first;
    private readonly ClipHit _second;
    private readonly byte _count;
    private int _index;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ClipFrameEnumerator(scoped in ClipHit first)
    {
        _first = first;
        _second = default;
        _count = 1;
        _index = -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ClipFrameEnumerator(scoped in ClipHit first, scoped in ClipHit second)
    {
        _first = first;
        _second = second;
        _count = 2;
        _index = -1;
    }

    public readonly ClipHit Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _index == 0 ? _first : _second;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        int next = _index + 1;

        if ((uint)next >= _count)
        {
            return false;
        }

        _index = next;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ClipFrameEnumerator GetEnumerator() => this;
}
