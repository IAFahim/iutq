using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Iutq.Core;

public readonly ref struct ClipQuery<TClip>
    where TClip : unmanaged
{
    private readonly DatabaseView _db;
    private readonly int _directoryBase;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ClipQuery(DatabaseView db, int typeSlot)
    {
        _db = db;
        _directoryBase = checked(typeSlot * db.Timelines.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<TrackInstance> Tracks(TimelineIndex timelineIndex)
    {
        if ((uint)timelineIndex.Value >= (uint)_db.Timelines.Length) return ReadOnlySpan<TrackInstance>.Empty;

        return TracksCore(timelineIndex.Value);
    }

    /// <summary>
    ///     Directory lookup for an already-validated timeline id
    ///     (<see cref="DatabaseView.TryGetTimeline" /> or the public
    ///     <see cref="Tracks" /> performed the range check).
    ///     Directory contiguity is validated on load: index and slice
    ///     without re-checking bounds against the whole tracks section.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<TrackInstance> TracksCore(int timelineIndex)
    {
        ref var directory = ref MemoryMarshal.GetReference(_db.Directory);
        var partition = Unsafe.Add(ref directory, (nint)(uint)(_directoryBase + timelineIndex));
        ref var first = ref MemoryMarshal.GetReference(_db.Tracks);
        return MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.Add(ref first, (nint)(uint)partition.TrackStart),
            partition.TrackCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly TrackTemplate TrackTemplate(in TrackInstance track)
    {
        return ref _db.TrackTemplate[track.TrackTemplateId];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TrackRef Track(in TrackInstance track)
    {
        return new TrackRef(in track, in _db.TrackTemplate[track.TrackTemplateId]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly TClip Data(int dataOffset)
    {
        return ref _db.Payload<TClip>(dataOffset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly TClip Data(in ClipSample sample)
    {
        return ref _db.Payload<TClip>(sample.DataOffset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly TClip Data(in ClipFrame frame)
    {
        return ref _db.Payload<TClip>(frame.DataOffset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly TClip Data(in ClipTransition transition)
    {
        return ref _db.Payload<TClip>(transition.DataOffset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ClipSampleEnumerator Sample(in TrackInstance track, int tick)
    {
        ref readonly var data = ref TrackTemplateRef(in track);

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
        var samples = Sample(in track, tick);

        while (samples.MoveNext())
        {
            var sample = samples.Current;
            ref readonly var clip = ref _db.Payload<TClip>(sample.DataOffset);
            visitor.Sample(in track, in clip, sample.Weight);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Sample<TVisitor>(
        ReadOnlySpan<TimelineCursor> timelines,
        ref TVisitor visitor)
        where TVisitor : struct, IClipSampleVisitor<TClip>
    {
        foreach (ref readonly var cursor in timelines)
        {
            var tracks = Tracks(cursor.Timeline);

            foreach (ref readonly var track in tracks)
            {
                var samples = Sample(in track, cursor.Tick);

                while (samples.MoveNext())
                {
                    var sample = samples.Current;
                    ref readonly var clip = ref _db.Payload<TClip>(sample.DataOffset);
                    visitor.Sample(in track, in clip, sample.Weight);
                }
            }
        }
    }

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
        ref readonly var data = ref TrackTemplateRef(in track);

        return data.Mode == TrackMode.Exclusive
            ? FrameExclusive(in data, tick, direction)
            : FrameCrossFade(in data, tick, direction);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void VisitFrames<TVisitor>(
        in TrackInstance track,
        int tick,
        TimelineDirection direction,
        ref TVisitor visitor)
        where TVisitor : struct, IClipFrameVisitor<TClip>
    {
        var frames = VisitFrames(in track, tick, direction);

        while (frames.MoveNext())
        {
            var frame = frames.Current;
            ref readonly var clip = ref _db.Payload<TClip>(frame.DataOffset);
            visitor.Visit(in track, in frame, in clip);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void VisitFrames<TVisitor>(
        ReadOnlySpan<TimelineCursor> timelines,
        ref TVisitor visitor)
        where TVisitor : struct, IClipFrameVisitor<TClip>
    {
        foreach (ref readonly var cursor in timelines)
        {
            var tracks = Tracks(cursor.Timeline);

            foreach (ref readonly var track in tracks)
            {
                var frames = VisitFrames(in track, in cursor);

                while (frames.MoveNext())
                {
                    var frame = frames.Current;
                    ref readonly var clip = ref _db.Payload<TClip>(frame.DataOffset);
                    visitor.Visit(in track, in frame, in clip);
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
            !_db.TryGetTimeline(span.Timeline, out var timeline))
            return 0;

        var tracks = TracksCore(span.Timeline.Value);

        if (tracks.IsEmpty) return 0;

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
            !_db.TryGetTimeline(span.Timeline, out var timeline))
            return 0;

        var single = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in track), 1);

        return span.CurrentRawTick > span.PreviousRawTick
            ? TraverseForward(single, in timeline, span.PreviousRawTick, span.CurrentRawTick, ref visitor)
            : TraverseReverse(single, in timeline, span.PreviousRawTick, span.CurrentRawTick, ref visitor);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ClipSampleEnumerator SampleExclusive(in TrackTemplate data, int tick)
    {
        var clips = ClipSlice(in data, 0, data.ClipCount);
        var index = ActiveClipIndex(clips, tick);

        if (index < 0) return default;

        ref var first = ref MemoryMarshal.GetReference(clips);
        ref readonly var clip = ref Unsafe.Add(ref first, (nint)(uint)index);
        return new ClipSampleEnumerator(new ClipSample(clip.DataOffset, 1f));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ClipSampleEnumerator SampleCrossFade(in TrackTemplate data, int tick)
    {
        var laneA = ClipSlice(in data, 0, data.LaneSplit);
        var laneB = ClipSlice(in data, data.LaneSplit, data.ClipCount - data.LaneSplit);

        var indexA = ActiveClipIndex(laneA, tick);
        var indexB = ActiveClipIndex(laneB, tick);

        if (indexA < 0 && indexB < 0) return default;

        ref var firstA = ref MemoryMarshal.GetReference(laneA);
        ref var firstB = ref MemoryMarshal.GetReference(laneB);

        if (indexB < 0)
            return new ClipSampleEnumerator(new ClipSample(
                Unsafe.Add(ref firstA, (nint)(uint)indexA).DataOffset, 1f));

        if (indexA < 0)
            return new ClipSampleEnumerator(new ClipSample(
                Unsafe.Add(ref firstB, (nint)(uint)indexB).DataOffset, 1f));

        ref readonly var clipA = ref Unsafe.Add(ref firstA, (nint)(uint)indexA);
        ref readonly var clipB = ref Unsafe.Add(ref firstB, (nint)(uint)indexB);
        var blendStart = Math.Max(clipA.Start, clipB.Start);
        var blendEnd = Math.Min(clipA.End, clipB.End);
        var factor = TimelineMath.BlendFactor(tick, blendStart, blendEnd);

        var weightB = clipB.Start >= clipA.Start ? factor : 1f - factor;
        var weightA = 1f - weightB;

        return new ClipSampleEnumerator(
            new ClipSample(clipA.DataOffset, weightA),
            new ClipSample(clipB.DataOffset, weightB));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ClipFrameEnumerator FrameExclusive(
        in TrackTemplate data,
        int tick,
        TimelineDirection direction)
    {
        var clips = ClipSlice(in data, 0, data.ClipCount);
        var index = ActiveClipIndex(clips, tick);

        if (index < 0) return default;

        ref var first = ref MemoryMarshal.GetReference(clips);
        ref readonly var clip = ref Unsafe.Add(ref first, (nint)(uint)index);
        var frame = CreateFrame(
            in clip,
            tick,
            direction,
            BlendPhase.None,
            1f,
            0f);

        return new ClipFrameEnumerator(in frame);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ClipFrameEnumerator FrameCrossFade(
        in TrackTemplate data,
        int tick,
        TimelineDirection direction)
    {
        var laneA = ClipSlice(in data, 0, data.LaneSplit);
        var laneB = ClipSlice(in data, data.LaneSplit, data.ClipCount - data.LaneSplit);

        var indexA = ActiveClipIndex(laneA, tick);
        var indexB = ActiveClipIndex(laneB, tick);

        if (indexA < 0 && indexB < 0) return default;

        ref var firstA = ref MemoryMarshal.GetReference(laneA);
        ref var firstB = ref MemoryMarshal.GetReference(laneB);

        if (indexB < 0)
        {
            ref readonly var clip = ref Unsafe.Add(ref firstA, (nint)(uint)indexA);
            var frame = CreateFrame(
                in clip,
                tick,
                direction,
                BlendPhase.None,
                1f,
                0f);
            return new ClipFrameEnumerator(in frame);
        }

        if (indexA < 0)
        {
            ref readonly var clip = ref Unsafe.Add(ref firstB, (nint)(uint)indexB);
            var frame = CreateFrame(
                in clip,
                tick,
                direction,
                BlendPhase.None,
                1f,
                0f);
            return new ClipFrameEnumerator(in frame);
        }

        ref readonly var clipA = ref Unsafe.Add(ref firstA, (nint)(uint)indexA);
        ref readonly var clipB = ref Unsafe.Add(ref firstB, (nint)(uint)indexB);
        var blendStart = Math.Max(clipA.Start, clipB.Start);
        var blendEnd = Math.Min(clipA.End, clipB.End);
        var factor = TimelineMath.BlendFactor(tick, blendStart, blendEnd);
        var blendPhase = TimelineMath.BlendPhaseAt(
            tick,
            blendStart,
            blendEnd,
            direction);

        var weightB = clipB.Start >= clipA.Start ? factor : 1f - factor;
        var weightA = 1f - weightB;

        var frameA = CreateFrame(
            in clipA,
            tick,
            direction,
            blendPhase,
            weightA,
            factor);
        var frameB = CreateFrame(
            in clipB,
            tick,
            direction,
            blendPhase,
            weightB,
            factor);

        return new ClipFrameEnumerator(in frameA, in frameB);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ClipFrame CreateFrame(
        in ClipEntry clip,
        int tick,
        TimelineDirection direction,
        BlendPhase blendPhase,
        float weight,
        float blendFactor)
    {
        return new ClipFrame(
            clip.Start,
            clip.End,
            tick,
            clip.DataOffset,
            direction,
            blendPhase,
            clip.Ease,
            weight,
            blendFactor);
    }

    /// <summary>
    ///     Track instances come from validated track sections: their
    ///     <see cref="TrackInstance.TrackTemplateId" /> is trusted by the kernel.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref readonly TrackTemplate TrackTemplateRef(in TrackInstance track)
    {
        ref var first = ref MemoryMarshal.GetReference(_db.TrackTemplate);
        return ref Unsafe.Add(ref first, (nint)(uint)track.TrackTemplateId);
    }

    /// <summary>
    ///     Clip windows are validated on load: build the span without re-checking
    ///     bounds against the whole clips section.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<ClipEntry> ClipSlice(in TrackTemplate data, int offset, int count)
    {
        ref var first = ref MemoryMarshal.GetReference(_db.Clips);
        return MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.Add(ref first, (nint)(data.ClipStart + offset)),
            count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ActiveClipIndex(ReadOnlySpan<ClipEntry> clips, int tick)
    {
        ref var first = ref MemoryMarshal.GetReference(clips);
        var lo = 0;
        var hi = clips.Length - 1;

        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            ref readonly var clip = ref Unsafe.Add(ref first, (nint)(uint)mid);

            if (tick < clip.Start)
                hi = mid - 1;
            else if (tick >= clip.End)
                lo = mid + 1;
            else
                return mid;
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
        var firstRaw = previousRawTick + 1;
        var lastRaw = currentRawTick;

        if ((timeline.Flags & TimelineFlags.Loop) == 0)
        {
            var first = Math.Max(firstRaw, 0);
            var last = Math.Min(lastRaw, timeline.Duration - 1L);

            if (first > last) return 0;

            return EmitRange(
                tracks,
                (int)first,
                (int)last,
                0,
                timeline.Duration,
                TimelineDirection.Forward,
                ref visitor);
        }

        var duration = timeline.Duration;

        if ((ulong)firstRaw < (ulong)duration && (ulong)lastRaw < (ulong)duration)
            return EmitRange(
                tracks,
                (int)firstRaw,
                (int)lastRaw,
                0,
                duration,
                TimelineDirection.Forward,
                ref visitor);

        var firstCycle = FloorDiv(firstRaw, duration);
        var lastCycle = FloorDiv(lastRaw, duration);

        if (firstCycle == lastCycle)
            return EmitRange(
                tracks,
                LocalTick(firstRaw, firstCycle, duration),
                LocalTick(lastRaw, lastCycle, duration),
                firstCycle,
                duration,
                TimelineDirection.Forward,
                ref visitor);

        var emitted = EmitRange(
            tracks,
            LocalTick(firstRaw, firstCycle, duration),
            duration - 1,
            firstCycle,
            duration,
            TimelineDirection.Forward,
            ref visitor);

        for (var cycle = firstCycle + 1; cycle < lastCycle; cycle++)
            emitted += EmitRange(
                tracks,
                0,
                duration - 1,
                cycle,
                duration,
                TimelineDirection.Forward,
                ref visitor);

        return emitted + EmitRange(
            tracks,
            0,
            LocalTick(lastRaw, lastCycle, duration),
            lastCycle,
            duration,
            TimelineDirection.Forward,
            ref visitor);
    }

    private long TraverseReverse<TVisitor>(
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

            return EmitRange(
                tracks,
                (int)low,
                (int)high,
                0,
                timeline.Duration,
                TimelineDirection.Reverse,
                ref visitor);
        }

        var duration = timeline.Duration;

        if ((ulong)lowRaw < (ulong)duration && (ulong)highRaw < (ulong)duration)
            return EmitRange(
                tracks,
                (int)lowRaw,
                (int)highRaw,
                0,
                duration,
                TimelineDirection.Reverse,
                ref visitor);

        var lowCycle = FloorDiv(lowRaw, duration);
        var highCycle = FloorDiv(highRaw, duration);

        if (lowCycle == highCycle)
            return EmitRange(
                tracks,
                LocalTick(lowRaw, lowCycle, duration),
                LocalTick(highRaw, highCycle, duration),
                lowCycle,
                duration,
                TimelineDirection.Reverse,
                ref visitor);

        var emitted = EmitRange(
            tracks,
            0,
            LocalTick(highRaw, highCycle, duration),
            highCycle,
            duration,
            TimelineDirection.Reverse,
            ref visitor);

        for (var cycle = highCycle - 1; cycle > lowCycle; cycle--)
            emitted += EmitRange(
                tracks,
                0,
                duration - 1,
                cycle,
                duration,
                TimelineDirection.Reverse,
                ref visitor);

        return emitted + EmitRange(
            tracks,
            LocalTick(lowRaw, lowCycle, duration),
            duration - 1,
            lowCycle,
            duration,
            TimelineDirection.Reverse,
            ref visitor);
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

        foreach (ref readonly var track in tracks)
        {
            ref readonly var data = ref TrackTemplateRef(in track);
            ref var boundariesFirst = ref MemoryMarshal.GetReference(_db.Boundaries);
            var boundaries = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.Add(ref boundariesFirst, (nint)(uint)data.BoundaryStart),
                data.BoundaryCount);

            emitted += direction == TimelineDirection.Forward
                ? EmitForwardTrack(in track, in data, boundaries, localLow, localHigh, cycle, duration, ref visitor)
                : EmitReverseTrack(in track, in data, boundaries, localLow, localHigh, cycle, duration, ref visitor);
        }

        return emitted;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long EmitForwardTrack<TVisitor>(
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

            EmitBoundary(in track, in data, in boundary, cycle, duration, TimelineDirection.Forward, ref visitor);
            emitted++;
        }

        return emitted;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long EmitReverseTrack<TVisitor>(
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

            EmitBoundary(in track, in data, in boundary, cycle, duration, TimelineDirection.Reverse, ref visitor);
            emitted++;
        }

        return emitted;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EmitBoundary<TVisitor>(
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
        ref var clipBase = ref MemoryMarshal.GetReference(_db.Clips);
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
                ref readonly var payload = ref _db.Payload<TClip>(clipA.DataOffset);
                visitor.OnClipTransition(in track, in transition, in payload);
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
                ref readonly var payload = ref _db.Payload<TClip>(clipA.DataOffset);
                visitor.OnClipTransition(in track, in transition, in payload);
                return;
            }

            case BoundaryKind.BlendInstant:
            case BoundaryKind.BlendStart:
            case BoundaryKind.BlendEnd:
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

                ref readonly var payloadA = ref _db.Payload<TClip>(clipA.DataOffset);
                ref readonly var payloadB = ref _db.Payload<TClip>(clipB.DataOffset);
                visitor.OnBlendTransition(in track, in transition, in payloadA, in payloadB);
                return;
            }
        }
    }

    /// <summary>
    ///     Boundary windows are short for typical tracks (a few entries per clip):
    ///     scan linearly below the threshold, binary-search above it.
    /// </summary>
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
    private static int LocalTick(long rawTick, long cycle, int duration)
    {
        return (int)(rawTick - cycle * duration);
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
        var next = _index + 1;

        if ((uint)next >= _count) return false;

        _index = next;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ClipSampleEnumerator GetEnumerator()
    {
        return this;
    }
}

public ref struct ClipFrameEnumerator
{
    private readonly ClipFrame _first;
    private readonly ClipFrame _second;
    private readonly byte _count;
    private int _index;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ClipFrameEnumerator(scoped in ClipFrame first)
    {
        _first = first;
        _second = default;
        _count = 1;
        _index = -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ClipFrameEnumerator(scoped in ClipFrame first, scoped in ClipFrame second)
    {
        _first = first;
        _second = second;
        _count = 2;
        _index = -1;
    }

    public readonly ClipFrame Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _index == 0 ? _first : _second;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        var next = _index + 1;

        if ((uint)next >= _count) return false;

        _index = next;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ClipFrameEnumerator GetEnumerator()
    {
        return this;
    }
}