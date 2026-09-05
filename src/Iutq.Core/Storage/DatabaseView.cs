using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Iutq.Core.Primitives;
using Iutq.Core.Querying;

namespace Iutq.Core.Storage;

public readonly ref struct DatabaseView
{
    public readonly ReadOnlySpan<TimelineHeader> Timelines;
    public readonly ReadOnlySpan<TimelineLookupEntry> TimelineLookup;
    public readonly ReadOnlySpan<TrackInstance> Tracks;
    public readonly ReadOnlySpan<TrackTemplate> TrackTemplate;
    public readonly ReadOnlySpan<ClipEntry> Clips;
    internal readonly ReadOnlySpan<TrackBoundary> Boundaries;
    public readonly ReadOnlySpan<TypeDescriptor> Types;
    public readonly ReadOnlySpan<TrackSpan> Directory;
    public readonly ReadOnlySpan<byte> Arena;

    internal DatabaseView(
        ReadOnlySpan<TimelineHeader> timelines,
        ReadOnlySpan<TimelineLookupEntry> timelineLookup,
        ReadOnlySpan<TrackInstance> tracks,
        ReadOnlySpan<TrackTemplate> trackData,
        ReadOnlySpan<ClipEntry> clips,
        ReadOnlySpan<TrackBoundary> boundaries,
        ReadOnlySpan<TypeDescriptor> types,
        ReadOnlySpan<TrackSpan> directory,
        ReadOnlySpan<byte> arena)
    {
        Timelines = timelines;
        TimelineLookup = timelineLookup;
        Tracks = tracks;
        TrackTemplate = trackData;
        Clips = clips;
        Boundaries = boundaries;
        Types = types;
        Directory = directory;
        Arena = arena;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryResolve(TimelineKey key, out TimelineIndex timelineIndex)
    {
        var lo = 0;
        var hi = TimelineLookup.Length - 1;

        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            ref readonly var entry = ref TimelineLookup[mid];

            if (key.Value < entry.Key)
            {
                hi = mid - 1;
            }
            else if (key.Value > entry.Key)
            {
                lo = mid + 1;
            }
            else
            {
                timelineIndex = new TimelineIndex(entry.TimelineIndex);
                return true;
            }
        }

        timelineIndex = TimelineIndex.None;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryResolve<TClip>(ClipType<TClip> type, out ClipTypeHandle<TClip> handle)
        where TClip : unmanaged
    {
        var lo = 0;
        var hi = Types.Length - 1;

        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            ref readonly var descriptor = ref Types[mid];

            if (type.Key > descriptor.Key)
            {
                lo = mid + 1;
            }
            else if (type.Key < descriptor.Key)
            {
                hi = mid - 1;
            }
            else if (descriptor.Size == Unsafe.SizeOf<TClip>())
            {
                handle = new ClipTypeHandle<TClip>(mid);
                return true;
            }
            else
            {
                break;
            }
        }

        handle = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ClipQuery<TClip> Query<TClip>(ClipTypeHandle<TClip> handle) where TClip : unmanaged
    {
        if (handle.IsValid && (uint)handle.TypeSlot < (uint)Types.Length)
            return new ClipQuery<TClip>(this, handle.TypeSlot);
        Check.HandleUsable(in handle);
        return default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetTimeline(TimelineIndex timelineIndex, out TimelineHeader timeline)
    {
        if ((uint)timelineIndex.Value >= (uint)Timelines.Length)
        {
            timeline = default;
            return false;
        }

        timeline = Unsafe.Add(ref MemoryMarshal.GetReference(Timelines), (nint)(uint)timelineIndex.Value);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly T Payload<T>(int dataOffset) where T : unmanaged
    {
        ref var arena = ref MemoryMarshal.GetReference(Arena);
        return ref Unsafe.As<byte, T>(ref Unsafe.Add(ref arena, dataOffset * Format.PayloadUnit));
    }
}
