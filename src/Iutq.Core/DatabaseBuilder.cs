using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Iutq.Core;

public sealed class DatabaseBuilder
{
    private readonly ArenaBuilder _arena = new();
    private readonly List<BoundaryHeader> _boundaryStorage = [];
    private readonly List<ClipHeader> _clipStorage = [];
    private readonly Dictionary<ulong, List<ClipWindow>> _clipWindowBuckets = [];
    private readonly HashSet<ulong> _timelineKeys = [];
    private readonly List<TimelineDraft> _timelines = [];
    private readonly List<TrackData> _trackData = [];

    private readonly Dictionary<(ulong TypeKey, TrackMode Mode, int ClipStart, int ClipCount, int LaneSplit), int>
        _trackDataIndex = [];

    private readonly List<StagedTrack> _tracks = [];
    private readonly Dictionary<ulong, TypeRegistration> _typeOwners = [];
    private int _sequence;

    public TimelineId AddTimeline(
        TimelineKey key,
        int duration,
        TimelineFlags flags = TimelineFlags.None)
    {
        if (key.Value == 0) throw new ArgumentOutOfRangeException(nameof(key));

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(duration);

        if (((byte)flags & ~(byte)TimelineFlags.Loop) != 0) throw new ArgumentOutOfRangeException(nameof(flags));

        if (!_timelineKeys.Add(key.Value))
            throw new ArgumentException($"Duplicate timeline key 0x{key.Value:X16}.", nameof(key));

        var id = _timelines.Count;
        _timelines.Add(new TimelineDraft(key.Value, duration, flags));
        return new TimelineId(id);
    }

    public void AddTrack<TClip>(
        TimelineId timelineId,
        ClipType<TClip> type,
        BindingId binding,
        TrackMode mode,
        ReadOnlySpan<ClipDefinition<TClip>> clips)
        where TClip : unmanaged
    {
        if ((uint)timelineId.Value >= (uint)_timelines.Count) throw new ArgumentOutOfRangeException(nameof(timelineId));

        if (clips.IsEmpty) throw new ArgumentException("Track must contain at least one clip.", nameof(clips));

        if (binding.Value < 0) throw new ArgumentOutOfRangeException(nameof(binding));

        if (mode != TrackMode.Exclusive && mode != TrackMode.CrossFade)
            throw new ArgumentOutOfRangeException(nameof(mode));

        RegisterType(type);
        var timeline = _timelines[timelineId.Value];
        var sorted = clips.ToArray();

        Array.Sort(sorted, static (a, b) =>
        {
            var start = a.Start.CompareTo(b.Start);
            return start != 0 ? start : a.End.CompareTo(b.End);
        });

        List<ClipHeader> laneA = [];
        List<ClipHeader> laneB = [];

        if (mode == TrackMode.Exclusive)
            BuildExclusive(in timeline, sorted, laneA);
        else
            BuildCrossFade(in timeline, sorted, laneA, laneB);

        ClipHeader[] headers = [.. laneA, .. laneB];
        var clipStart = InternClipHeaders(headers);
        var trackDataId = InternTrackData(
            type.Key,
            mode,
            clipStart,
            headers.Length,
            laneA.Count,
            headers);

        _tracks.Add(new StagedTrack(
            timelineId.Value,
            type.Key,
            binding.Value,
            trackDataId,
            _sequence++));
    }

    public TimelineDatabase Build()
    {
        var types = CollectTypes();
        var typeSlots = CreateTypeSlots(types);

        List<StagedTrack> orderedTracks = [.. _tracks];
        orderedTracks.Sort((a, b) =>
        {
            var type = typeSlots[a.TypeKey].CompareTo(typeSlots[b.TypeKey]);
            if (type != 0) return type;

            var timeline = a.TimelineId.CompareTo(b.TimelineId);
            if (timeline != 0) return timeline;

            var binding = a.Binding.CompareTo(b.Binding);
            return binding != 0 ? binding : a.Sequence.CompareTo(b.Sequence);
        });

        var timelines = new TimelineHeader[_timelines.Count];
        var lookup = new TimelineLookupEntry[_timelines.Count];
        var tracks = new TrackInstance[orderedTracks.Count];
        var directory = new TypePartition[checked(types.Length * _timelines.Count)];

        for (var i = 0; i < _timelines.Count; i++)
        {
            var timeline = _timelines[i];
            timelines[i] = new TimelineHeader(timeline.Key, timeline.Duration, timeline.Flags);
            lookup[i] = new TimelineLookupEntry(timeline.Key, i);
        }

        Array.Sort(lookup, static (a, b) => a.Key.CompareTo(b.Key));

        for (var i = 0; i < orderedTracks.Count; i++)
        {
            var staged = orderedTracks[i];
            tracks[i] = new TrackInstance(staged.Binding, staged.TrackDataId);

            var typeSlot = typeSlots[staged.TypeKey];
            var directoryIndex = checked(typeSlot * _timelines.Count + staged.TimelineId);
            var partition = directory[directoryIndex];

            directory[directoryIndex] = partition.TrackCount == 0
                ? new TypePartition(i, 1)
                : new TypePartition(partition.TrackStart, checked(partition.TrackCount + 1));
        }

        return TimelineDatabase.Create(
            TimelineDatabase.Assemble(
                timelines,
                lookup,
                tracks,
                [.. _trackData],
                [.. _clipStorage],
                [.. _boundaryStorage],
                types,
                directory,
                _arena.Build()));
    }

    private void RegisterType<TClip>(ClipType<TClip> type)
        where TClip : unmanaged
    {
        var runtimeType = typeof(TClip);
        var size = Unsafe.SizeOf<TClip>();

        if (_typeOwners.TryGetValue(type.Key, out var owner))
        {
            if (owner.Type != runtimeType || owner.Size != size)
                throw new InvalidOperationException(
                    $"Timeline type key 0x{type.Key:X16} is shared by incompatible clip payloads.");

            return;
        }

        var layout = runtimeType.StructLayoutAttribute;

        if (layout is null ||
            layout.Value != LayoutKind.Sequential ||
            layout.Pack != 1)
            throw new InvalidOperationException(
                $"Timeline clip {runtimeType.FullName} must use StructLayout(LayoutKind.Sequential, Pack = 1).");

        _typeOwners.Add(type.Key, new TypeRegistration(runtimeType, size));
    }

    private TypeDescriptor[] CollectTypes()
    {
        var result = new TypeDescriptor[_typeOwners.Count];
        var index = 0;

        foreach (var pair in _typeOwners) result[index++] = new TypeDescriptor(pair.Key, pair.Value.Size);

        Array.Sort(result, static (a, b) => a.Key.CompareTo(b.Key));
        return result;
    }

    private static Dictionary<ulong, int> CreateTypeSlots(TypeDescriptor[] types)
    {
        Dictionary<ulong, int> slots = new(types.Length);

        for (var i = 0; i < types.Length; i++) slots.Add(types[i].Key, i);

        return slots;
    }

    private void BuildExclusive<TClip>(
        in TimelineDraft timeline,
        ReadOnlySpan<ClipDefinition<TClip>> clips,
        List<ClipHeader> lane)
        where TClip : unmanaged
    {
        var previousEnd = 0;

        foreach (var definition in clips)
        {
            ValidateClip(in timeline, in definition);

            if (definition.Start < previousEnd)
                throw new ArgumentException(
                    $"Exclusive clip [{definition.Start}, {definition.End}) overlaps the previous clip.");

            lane.Add(CreateClip(in definition));
            previousEnd = definition.End;
        }
    }

    private void BuildCrossFade<TClip>(
        in TimelineDraft timeline,
        ReadOnlySpan<ClipDefinition<TClip>> clips,
        List<ClipHeader> laneA,
        List<ClipHeader> laneB)
        where TClip : unmanaged
    {
        var endA = 0;
        var endB = 0;

        foreach (var definition in clips)
        {
            ValidateClip(in timeline, in definition);
            var header = CreateClip(in definition);

            if (definition.Start >= endA)
            {
                laneA.Add(header);
                endA = definition.End;
            }
            else if (definition.Start >= endB)
            {
                laneB.Add(header);
                endB = definition.End;
            }
            else
            {
                throw new ArgumentException(
                    $"CrossFade track has more than two clips active at tick {definition.Start}.");
            }
        }
    }

    private static void ValidateClip<TClip>(
        in TimelineDraft timeline,
        in ClipDefinition<TClip> definition)
        where TClip : unmanaged
    {
        if (definition.Start < 0 || definition.End <= definition.Start)
            throw new ArgumentException(
                $"Clip [{definition.Start}, {definition.End}) is invalid.");

        if (definition.End > timeline.Duration)
            throw new ArgumentOutOfRangeException(
                nameof(definition),
                $"Clip end {definition.End} exceeds timeline duration {timeline.Duration}.");

        if ((byte)definition.Ease > (byte)ClipEase.CubicInOut)
            throw new ArgumentOutOfRangeException(nameof(definition));
    }

    private ClipHeader CreateClip<TClip>(in ClipDefinition<TClip> definition)
        where TClip : unmanaged
    {
        var data = definition.Data;
        var dataOffset = _arena.Intern(in data);
        return new ClipHeader(definition.Start, definition.End, dataOffset, definition.Ease);
    }

    private int InternClipHeaders(ClipHeader[] headers)
    {
        var bytes = MemoryMarshal.AsBytes(headers);
        var hash = Fnv1A64.Hash(bytes);

        if (_clipWindowBuckets.TryGetValue(hash, out var candidates))
        {
            var storage = CollectionsMarshal.AsSpan(_clipStorage);

            foreach (var candidate in candidates)
                if (candidate.Count == headers.Length &&
                    bytes.SequenceEqual(MemoryMarshal.AsBytes(storage.Slice(candidate.Start, candidate.Count))))
                    return candidate.Start;
        }
        else
        {
            candidates = [];
            _clipWindowBuckets.Add(hash, candidates);
        }

        var start = _clipStorage.Count;
        _clipStorage.AddRange(headers);
        candidates.Add(new ClipWindow(start, headers.Length));
        return start;
    }

    private int InternTrackData(
        ulong typeKey,
        TrackMode mode,
        int clipStart,
        int clipCount,
        int laneSplit,
        ReadOnlySpan<ClipHeader> headers)
    {
        (ulong TypeKey, TrackMode Mode, int ClipStart, int ClipCount, int LaneSplit) key =
            (typeKey, mode, clipStart, clipCount, laneSplit);

        if (_trackDataIndex.TryGetValue(key, out var existing)) return existing;

        var boundaries = BuildBoundaries(headers, laneSplit, mode);
        var boundaryStart = _boundaryStorage.Count;
        _boundaryStorage.AddRange(boundaries);

        var id = _trackData.Count;
        _trackData.Add(new TrackData(
            typeKey,
            clipStart,
            clipCount,
            laneSplit,
            boundaryStart,
            boundaries.Length,
            mode));
        _trackDataIndex.Add(key, id);
        return id;
    }

    private static BoundaryHeader[] BuildBoundaries(
        ReadOnlySpan<ClipHeader> clips,
        int laneSplit,
        TrackMode mode)
    {
        List<BoundaryHeader> result = [];

        for (var i = 0; i < clips.Length; i++)
        {
            ref readonly var clip = ref clips[i];

            if (clip.Duration == 1)
            {
                result.Add(new BoundaryHeader(clip.Start, i, -1, BoundaryKind.ClipSingle));
            }
            else
            {
                result.Add(new BoundaryHeader(clip.Start, i, -1, BoundaryKind.ClipLeft));
                result.Add(new BoundaryHeader(clip.End - 1, i, -1, BoundaryKind.ClipRight));
            }
        }

        if (mode == TrackMode.CrossFade && laneSplit < clips.Length)
        {
            var a = 0;
            var b = laneSplit;

            while (a < laneSplit && b < clips.Length)
            {
                ref readonly var clipA = ref clips[a];
                ref readonly var clipB = ref clips[b];
                var start = Math.Max(clipA.Start, clipB.Start);
                var end = Math.Min(clipA.End, clipB.End);

                if (start < end)
                {
                    if (end - start == 1)
                    {
                        result.Add(new BoundaryHeader(start, a, b, BoundaryKind.BlendSingle));
                    }
                    else
                    {
                        result.Add(new BoundaryHeader(start, a, b, BoundaryKind.BlendLeft));
                        result.Add(new BoundaryHeader(end - 1, a, b, BoundaryKind.BlendRight));
                    }
                }

                if (clipA.End <= clipB.End) a++;
                else b++;
            }
        }

        result.Sort(static (a, b) =>
        {
            var tick = a.Tick.CompareTo(b.Tick);
            if (tick != 0) return tick;

            var kind = a.Kind.CompareTo(b.Kind);
            if (kind != 0) return kind;

            var clipA = a.ClipA.CompareTo(b.ClipA);
            return clipA != 0 ? clipA : a.ClipB.CompareTo(b.ClipB);
        });

        return [.. result];
    }

    private readonly record struct TimelineDraft(
        ulong Key,
        int Duration,
        TimelineFlags Flags);

    private sealed record StagedTrack(
        int TimelineId,
        ulong TypeKey,
        int Binding,
        int TrackDataId,
        int Sequence);

    private readonly record struct TypeRegistration(
        Type Type,
        int Size);

    private readonly record struct ClipWindow(
        int Start,
        int Count);
}