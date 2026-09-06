using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Iutq.Core.Primitives;
using Iutq.Core.Storage;

namespace Iutq.Core.Baking;

public sealed class DatabaseBuilder
{
    private readonly ArenaBuilder _arena = new();
    private readonly List<TrackBoundary> _boundaryStorage = [];
    private readonly List<ClipEntry> _clipStorage = [];
    private readonly Dictionary<ulong, List<ClipSlice>> _clipSliceBuckets = [];
    private readonly HashSet<ulong> _timelineKeys = [];
    private readonly List<TimelineDraft> _timelines = [];
    private readonly List<TemplateDraft> _trackData = [];

    private readonly Dictionary<(ulong TypeKey, TrackMode Mode, int ClipStart, int ClipCount, int LaneSplit), int>
        _trackDataIndex = [];

    private readonly List<StagedTrack> _tracks = [];
    private readonly Dictionary<ulong, TypeRegistration> _typeOwners = [];
    private int _sequence;

    public TimelineIndex AddTimeline(
        TimelineKey key,
        int duration,
        TimelineFlags flags = TimelineFlags.None)
    {
        if (key.Value == 0) throw new ArgumentOutOfRangeException(nameof(key));

        // CA1512 would demand ThrowIfNegativeOrZero, which Unity's BCL lacks;
        // explicit throws keep this file compilable when dropped into Unity.
#pragma warning disable CA1512
        if (duration <= 0) throw new ArgumentOutOfRangeException(nameof(duration));
#pragma warning restore CA1512

        if (((byte)flags & ~(byte)TimelineFlags.Loop) != 0) throw new ArgumentOutOfRangeException(nameof(flags));

        if (duration > Format.MaxTimelineDuration)
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                $"Timeline duration {duration} exceeds the blob v2 cap {Format.MaxTimelineDuration} ticks.");

        if (_timelines.Count == Format.MaxTimelines)
            throw new InvalidOperationException(
                $"iutq blob v2 cap exceeded: at most {Format.MaxTimelines} timelines per database.");

        if (!_timelineKeys.Add(key.Value))
            throw new ArgumentException($"Duplicate timeline key 0x{key.Value:X16}.", nameof(key));

        var id = _timelines.Count;
        _timelines.Add(new TimelineDraft(key.Value, duration, flags));
        return new TimelineIndex(id);
    }

    public void AddTrack<TClip>(
        TimelineIndex timelineIndex,
        ClipType<TClip> type,
        BindingId binding,
        TrackMode mode,
        ReadOnlySpan<ClipDefinition<TClip>> clips)
        where TClip : unmanaged
    {
        if ((uint)timelineIndex.Value >= (uint)_timelines.Count) throw new ArgumentOutOfRangeException(nameof(timelineIndex));

        if (clips.IsEmpty) throw new ArgumentException("Track must contain at least one clip.", nameof(clips));

        if (binding.Value < 0 || binding.Value > Format.MaxBinding)
            throw new ArgumentOutOfRangeException(
                nameof(binding),
                $"Binding {binding.Value} exceeds the blob v2 cap {Format.MaxBinding}.");

        if (mode != TrackMode.Exclusive && mode != TrackMode.CrossFade)
            throw new ArgumentOutOfRangeException(nameof(mode));

        RegisterType(type);
        var timeline = _timelines[timelineIndex.Value];
        var sorted = clips.ToArray();

        Array.Sort(sorted, static (a, b) =>
        {
            var start = a.Start.CompareTo(b.Start);
            return start != 0 ? start : a.End.CompareTo(b.End);
        });

        List<ClipEntry> laneA = [];
        List<ClipEntry> laneB = [];

        if (mode == TrackMode.Exclusive)
            BuildExclusive(in timeline, sorted, laneA);
        else
            BuildCrossFade(in timeline, sorted, laneA, laneB);

        ClipEntry[] headers = [.. laneA, .. laneB];
        var clipStart = InternClipEntrys(headers);
        var trackDataId = InternTrackTemplate(
            type.Key,
            mode,
            clipStart,
            headers.Length,
            laneA.Count,
            headers);

        _tracks.Add(new StagedTrack(
            timelineIndex.Value,
            type.Key,
            binding.Value,
            trackDataId,
            _sequence++));
    }

    public TimelineDatabase Build()
    {
        return Build(false);
    }

    /// <summary>
    ///     Bakes the database. With <paramref name="enableFastLookup" /> the
    ///     builder additionally emits per-template tick-to-clip LUTs, boundary
    ///     prefix tables and baked crossfade blend factors into an optional
    ///     fast-lookup section, trading blob size for O(1) sampling and
    ///     windowed traversal. Templates that exceed an area's 65,535-entry
    ///     cap, or that are shared across timelines of different durations,
    ///     silently fall back to the searched path.
    /// </summary>
    public TimelineDatabase Build(bool enableFastLookup)
    {
        CheckBuildCaps();
        var types = CollectTypes();
        var typeSlots = CreateTypeSlots(types);

        List<StagedTrack> orderedTracks = [.. _tracks];
        orderedTracks.Sort((a, b) =>
        {
            var type = typeSlots[a.TypeKey].CompareTo(typeSlots[b.TypeKey]);
            if (type != 0) return type;

            var timeline = a.TimelineIndex.CompareTo(b.TimelineIndex);
            if (timeline != 0) return timeline;

            var binding = a.Binding.CompareTo(b.Binding);
            return binding != 0 ? binding : a.Sequence.CompareTo(b.Sequence);
        });

        var timelines = new TimelineHeader[_timelines.Count];
        var lookup = new TimelineLookupEntry[_timelines.Count];
        var tracks = new TrackInstance[orderedTracks.Count];
        var templates = new TrackTemplate[_trackData.Count];
        var directory = new TrackSpan[checked(types.Length * _timelines.Count)];

        for (var i = 0; i < _timelines.Count; i++)
        {
            var timeline = _timelines[i];
            timelines[i] = new TimelineHeader((ushort)timeline.Duration, timeline.Flags);
            lookup[i] = new TimelineLookupEntry(timeline.Key, (ushort)i);
        }

        Array.Sort(lookup, static (a, b) => a.Key.CompareTo(b.Key));

        for (var i = 0; i < _trackData.Count; i++)
        {
            var draft = _trackData[i];
            templates[i] = new TrackTemplate(
                (ushort)typeSlots[draft.TypeKey],
                draft.ClipStart,
                draft.ClipCount,
                draft.LaneSplit,
                draft.BoundaryStart,
                draft.BoundaryCount,
                draft.Mode);
        }

        for (var i = 0; i < orderedTracks.Count; i++)
        {
            var staged = orderedTracks[i];
            tracks[i] = new TrackInstance((ushort)staged.Binding, (ushort)staged.TrackTemplateId);

            var typeSlot = typeSlots[staged.TypeKey];
            var directoryIndex = checked(typeSlot * _timelines.Count + staged.TimelineIndex);
            var partition = directory[directoryIndex];

            directory[directoryIndex] = partition.TrackCount == 0
                ? new TrackSpan((ushort)i, 1)
                : new TrackSpan(partition.TrackStart, checked((ushort)(partition.TrackCount + 1)));
        }

        return TimelineDatabase.Create(
            TimelineDatabase.Assemble(
                timelines,
                lookup,
                tracks,
                templates,
                [.. _clipStorage],
                [.. _boundaryStorage],
                types,
                directory,
                _arena.Build(),
                enableFastLookup ? BuildFastSection() : null));
    }

    private byte[]? BuildFastSection()
    {
        if (_trackData.Count == 0) return null;

        // 0 = no owning timeline seen yet, -1 = template shared across
        // timelines of different durations (structure must then stay searched).
        var durations = new int[_trackData.Count];

        foreach (var staged in _tracks)
        {
            var duration = _timelines[staged.TimelineIndex].Duration;
            var known = durations[staged.TrackTemplateId];
            durations[staged.TrackTemplateId] = known == 0 ? duration : known == duration ? known : -1;
        }

        const int MaxAreaEntries = ushort.MaxValue;

        // Below these sizes the searched path is already a short comparison
        // chain / linear scan over one cache line; a LUT indirection only adds
        // dependent loads. Mirrors the source-generated kernel's thresholds.
        const int ClipLutThreshold = 8;
        const int PrefixThreshold = 8;

        List<byte> lut8 = [];
        List<ushort> lut16 = [];
        List<ushort> prefix = [];
        List<float> factors = [];
        var descriptors = new TrackFastData[_trackData.Count];
        var clipSpan = CollectionsMarshal.AsSpan(_clipStorage);
        var boundarySpan = CollectionsMarshal.AsSpan(_boundaryStorage);

        for (var i = 0; i < _trackData.Count; i++)
        {
            var draft = _trackData[i];
            var duration = durations[i];
            var lut8Mark = lut8.Count;
            var lut16Mark = lut16.Count;
            var prefixMark = prefix.Count;
            var factorMark = factors.Count;

            byte width = 0;
            ushort lutStart = 0;
            ushort lutCount = 0;
            ushort prefixStart = 0;
            ushort prefixCount = 0;
            ushort factorStart = 0;

            var crossFade = draft.Mode == TrackMode.CrossFade;
            var slotsPerTick = crossFade ? 2 : 1;
            var laneMax = crossFade
                ? Math.Max(draft.LaneSplit, draft.ClipCount - draft.LaneSplit)
                : draft.ClipCount;
            var lutWanted = laneMax > ClipLutThreshold && duration > 0 && duration <= 2048;

            if (lutWanted &&
                lut16.Count + (long)slotsPerTick * duration <= MaxAreaEntries &&
                (!crossFade || factors.Count + duration <= MaxAreaEntries))
            {
                var window = clipSpan.Slice(draft.ClipStart, draft.ClipCount);
                var narrow = duration <= 256 && draft.ClipCount <= 254 &&
                             lut8.Count + (long)slotsPerTick * duration <= MaxAreaEntries;

                var filled = true;

                for (var tick = 0; tick < duration && filled; tick++)
                {
                    int indexA;
                    int indexB;

                    if (!crossFade)
                    {
                        indexA = FindActiveClip(window, tick);
                        indexB = -1;
                    }
                    else
                    {
                        var laneA = window[..draft.LaneSplit];
                        var laneB = window[draft.LaneSplit..];
                        indexA = FindActiveClip(laneA, tick);
                        indexB = FindActiveClip(laneB, tick);

                        // Lane-local index must become a template-window index.
                        if (indexB >= 0) indexB += draft.LaneSplit;
                    }

                    var entryA = (ushort)(indexA + 1);
                    var entryB = (ushort)(indexB + 1);

                    if (narrow && (entryA > byte.MaxValue || entryB > byte.MaxValue))
                    {
                        filled = false;
                        break;
                    }

                    if (crossFade)
                    {
                        float factor = 0f;

                        if (indexA >= 0 && indexB >= 0)
                        {
                            ref readonly var clipA = ref window[indexA];
                            ref readonly var clipB = ref window[indexB];
                            factor = TimelineMath.BlendFactor(
                                tick,
                                Math.Max(clipA.Start, clipB.Start),
                                Math.Min(clipA.End, clipB.End));
                        }

                        factors.Add(factor);
                    }

                    // Exclusive tracks occupy one slot per tick; crossfade
                    // tracks store the lane pair.
                    if (narrow)
                    {
                        lut8.Add((byte)entryA);
                        if (crossFade) lut8.Add((byte)entryB);
                    }
                    else
                    {
                        lut16.Add(entryA);
                        if (crossFade) lut16.Add(entryB);
                    }
                }

                if (filled)
                {
                    width = (narrow, crossFade) switch
                    {
                        (true, false) => 1,
                        (false, false) => 2,
                        (true, true) => 3,
                        _ => 4
                    };
                    lutStart = narrow ? (ushort)lut8Mark : (ushort)lut16Mark;
                    lutCount = (ushort)duration;
                    factorStart = (ushort)factorMark;
                }
            }

            // Prefix tables are independent of LUTs: long boundary windows pay
            // for a tick-indexed window even when sampling stays searched.
            if (draft.BoundaryCount > PrefixThreshold && duration > 0 &&
                prefix.Count + duration + 1L <= MaxAreaEntries)
            {
                var boundaries = boundarySpan.Slice(draft.BoundaryStart, draft.BoundaryCount);
                prefixStart = (ushort)prefix.Count;
                prefixCount = (ushort)(duration + 1);
                var boundaryIndex = 0;

                for (var tick = 0; tick <= duration; tick++)
                {
                    while (boundaryIndex < boundaries.Length && boundaries[boundaryIndex].Tick < tick) boundaryIndex++;

                    prefix.Add((ushort)boundaryIndex);
                }
            }
            else
            {
                prefix.RemoveRange(prefixMark, prefix.Count - prefixMark);
            }

            if (width == 0)
            {
                lut8.RemoveRange(lut8Mark, lut8.Count - lut8Mark);
                lut16.RemoveRange(lut16Mark, lut16.Count - lut16Mark);
                factors.RemoveRange(factorMark, factors.Count - factorMark);
            }

            descriptors[i] = new TrackFastData(lutStart, lutCount, prefixStart, prefixCount, factorStart, width);
        }

        // Nothing qualified: bake no section at all so the flag stays clear
        // and every query keeps the exact searched-path shape.
        if (lut8.Count == 0 && lut16.Count == 0 && prefix.Count == 0 && factors.Count == 0) return null;

        var header = new FastSectionHeader(
            (ushort)_trackData.Count,
            (ushort)lut8.Count,
            (ushort)lut16.Count,
            (ushort)prefix.Count,
            (ushort)factors.Count);
        var offsets = FastSectionOffsets.Compute(
            header.DescriptorCount,
            header.U8Entries,
            header.U16Entries,
            header.PrefixEntries,
            header.FactorFloats);
        var section = new byte[offsets.TotalBytes];

        MemoryMarshal.Write(section.AsSpan(), in header);
        MemoryMarshal.AsBytes(descriptors.AsSpan()).CopyTo(
            section.AsSpan(Unsafe.SizeOf<FastSectionHeader>()));
        MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(lut16)).CopyTo(section.AsSpan(offsets.U16Start));
        MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(prefix)).CopyTo(section.AsSpan(offsets.PrefixStart));
        MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(factors)).CopyTo(section.AsSpan(offsets.FactorStart));
        CollectionsMarshal.AsSpan(lut8).CopyTo(section.AsSpan(offsets.U8Start));

        return section;
    }

    /// <summary>
    ///     Same binary search the query kernel uses; duplicated here because
    ///     Baking may not reference Querying.
    /// </summary>
    private static int FindActiveClip(ReadOnlySpan<ClipEntry> clips, int tick)
    {
        var lo = 0;
        var hi = clips.Length - 1;

        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            ref readonly var clip = ref clips[mid];

            if (tick < clip.Start) hi = mid - 1;
            else if (tick >= clip.End) lo = mid + 1;
            else return mid;
        }

        return -1;
    }

    private void CheckBuildCaps()
    {
        if (_timelines.Count > Format.MaxTimelines ||
            _tracks.Count > Format.MaxTracks ||
            _trackData.Count > Format.MaxTrackTemplates ||
            _clipStorage.Count > Format.MaxClips ||
            _boundaryStorage.Count > Format.MaxBoundaries ||
            _typeOwners.Count > Format.MaxTypes)
            throw new InvalidOperationException(
                "iutq blob v2 cap exceeded: " +
                $"timelines {_timelines.Count}/{Format.MaxTimelines}, " +
                $"tracks {_tracks.Count}/{Format.MaxTracks}, " +
                $"templates {_trackData.Count}/{Format.MaxTrackTemplates}, " +
                $"clips {_clipStorage.Count}/{Format.MaxClips}, " +
                $"boundaries {_boundaryStorage.Count}/{Format.MaxBoundaries}, " +
                $"types {_typeOwners.Count}/{Format.MaxTypes}.");
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

        foreach (var pair in _typeOwners) result[index++] = new TypeDescriptor(pair.Key, (ushort)pair.Value.Size);

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
        List<ClipEntry> lane)
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
        List<ClipEntry> laneA,
        List<ClipEntry> laneB)
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

    private ClipEntry CreateClip<TClip>(in ClipDefinition<TClip> definition)
        where TClip : unmanaged
    {
        var data = definition.Data;
        var dataOffset = _arena.Intern(in data);
        return new ClipEntry(
            (ushort)definition.Start,
            (ushort)definition.End,
            checked((ushort)(dataOffset / Format.PayloadUnit)),
            definition.Ease);
    }

    private int InternClipEntrys(ClipEntry[] headers)
    {
        var bytes = MemoryMarshal.AsBytes(headers);
        var hash = Fnv1A64.Hash(bytes);

        if (_clipSliceBuckets.TryGetValue(hash, out var candidates))
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
            _clipSliceBuckets.Add(hash, candidates);
        }

        var start = _clipStorage.Count;
        _clipStorage.AddRange(headers);
        candidates.Add(new ClipSlice(start, headers.Length));
        return start;
    }

    private int InternTrackTemplate(
        ulong typeKey,
        TrackMode mode,
        int clipStart,
        int clipCount,
        int laneSplit,
        ReadOnlySpan<ClipEntry> headers)
    {
        (ulong TypeKey, TrackMode Mode, int ClipStart, int ClipCount, int LaneSplit) key =
            (typeKey, mode, clipStart, clipCount, laneSplit);

        if (_trackDataIndex.TryGetValue(key, out var existing)) return existing;

        var boundaries = BuildBoundaries(headers, laneSplit, mode);
        var boundaryStart = _boundaryStorage.Count;
        _boundaryStorage.AddRange(boundaries);

        var id = _trackData.Count;
        _trackData.Add(new TemplateDraft(
            typeKey,
            (ushort)clipStart,
            (ushort)clipCount,
            (ushort)laneSplit,
            (ushort)boundaryStart,
            (ushort)boundaries.Length,
            mode));
        _trackDataIndex.Add(key, id);
        return id;
    }

    private static TrackBoundary[] BuildBoundaries(
        ReadOnlySpan<ClipEntry> clips,
        int laneSplit,
        TrackMode mode)
    {
        List<TrackBoundary> result = [];

        for (var i = 0; i < clips.Length; i++)
        {
            ref readonly var clip = ref clips[i];

            if (clip.Duration == 1)
            {
                result.Add(new TrackBoundary(clip.Start, (ushort)i, TrackBoundary.NoClip, BoundaryKind.ClipInstant));
            }
            else
            {
                result.Add(new TrackBoundary(clip.Start, (ushort)i, TrackBoundary.NoClip, BoundaryKind.ClipStart));
                result.Add(new TrackBoundary(
                    (ushort)(clip.End - 1), (ushort)i, TrackBoundary.NoClip, BoundaryKind.ClipEnd));
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
                        result.Add(new TrackBoundary(start, (ushort)a, (ushort)b, BoundaryKind.BlendInstant));
                    }
                    else
                    {
                        result.Add(new TrackBoundary(start, (ushort)a, (ushort)b, BoundaryKind.BlendStart));
                        result.Add(new TrackBoundary((ushort)(end - 1), (ushort)a, (ushort)b, BoundaryKind.BlendEnd));
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

    private readonly record struct TemplateDraft(
        ulong TypeKey,
        ushort ClipStart,
        ushort ClipCount,
        ushort LaneSplit,
        ushort BoundaryStart,
        ushort BoundaryCount,
        TrackMode Mode);

    private sealed record StagedTrack(
        int TimelineIndex,
        ulong TypeKey,
        int Binding,
        int TrackTemplateId,
        int Sequence);

    private readonly record struct TypeRegistration(
        Type Type,
        int Size);

    private readonly record struct ClipSlice(
        int Start,
        int Count);
}