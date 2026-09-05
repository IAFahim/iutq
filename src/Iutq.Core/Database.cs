using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Iutq.Core;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct BlobHeader(
    uint magic,
    ushort version,
    ushort reserved,
    ushort timelineCount,
    ushort trackCount,
    ushort trackDataCount,
    ushort clipCount,
    ushort boundaryCount,
    ushort typeCount,
    ushort arenaUnits,
    uint payloadHash)
{
    public readonly uint Magic = magic;
    public readonly ushort Version = version;
    public readonly ushort Reserved = reserved;
    public readonly ushort TimelineCount = timelineCount;
    public readonly ushort TrackCount = trackCount;
    public readonly ushort TrackTemplateCount = trackDataCount;
    public readonly ushort ClipCount = clipCount;
    public readonly ushort BoundaryCount = boundaryCount;
    public readonly ushort TypeCount = typeCount;
    public readonly ushort ArenaUnits = arenaUnits;
    public readonly uint PayloadHash = payloadHash;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly unsafe struct DatabaseSections
{
    private readonly nint _timelines;
    private readonly int _timelineCount;
    private readonly nint _lookup;
    private readonly int _lookupCount;
    private readonly nint _tracks;
    private readonly int _trackCount;
    private readonly nint _trackData;
    private readonly int _trackDataCount;
    private readonly nint _clips;
    private readonly int _clipCount;
    private readonly nint _boundaries;
    private readonly int _boundaryCount;
    private readonly nint _types;
    private readonly int _typeCount;
    private readonly nint _directory;
    private readonly int _directoryCount;
    private readonly nint _arena;
    private readonly int _arenaBytes;

    private DatabaseSections(
        nint timelines, int timelineCount,
        nint lookup, int lookupCount,
        nint tracks, int trackCount,
        nint trackData, int trackDataCount,
        nint clips, int clipCount,
        nint boundaries, int boundaryCount,
        nint types, int typeCount,
        nint directory, int directoryCount,
        nint arena, int arenaBytes)
    {
        _timelines = timelines;
        _timelineCount = timelineCount;
        _lookup = lookup;
        _lookupCount = lookupCount;
        _tracks = tracks;
        _trackCount = trackCount;
        _trackData = trackData;
        _trackDataCount = trackDataCount;
        _clips = clips;
        _clipCount = clipCount;
        _boundaries = boundaries;
        _boundaryCount = boundaryCount;
        _types = types;
        _typeCount = typeCount;
        _directory = directory;
        _directoryCount = directoryCount;
        _arena = arena;
        _arenaBytes = arenaBytes;
    }

    internal ReadOnlySpan<TimelineHeader> Timelines => new((void*)_timelines, _timelineCount);

    internal ReadOnlySpan<TimelineLookupEntry> TimelineLookup => new((void*)_lookup, _lookupCount);

    internal ReadOnlySpan<TrackInstance> Tracks => new((void*)_tracks, _trackCount);

    internal ReadOnlySpan<TrackTemplate> TrackTemplate => new((void*)_trackData, _trackDataCount);

    internal ReadOnlySpan<ClipEntry> Clips => new((void*)_clips, _clipCount);

    internal ReadOnlySpan<TrackBoundary> Boundaries => new((void*)_boundaries, _boundaryCount);

    internal ReadOnlySpan<TypeDescriptor> Types => new((void*)_types, _typeCount);

    internal ReadOnlySpan<TrackSpan> Directory => new((void*)_directory, _directoryCount);

    internal ReadOnlySpan<byte> Arena => new((void*)_arena, _arenaBytes);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static DatabaseSections Compute(byte[] blob)
    {
        ReadOnlySpan<byte> span = blob;
        var header = MemoryMarshal.Read<BlobHeader>(span);
        var layout = SectionLayout.Compute(
            header.TimelineCount,
            header.TrackCount,
            header.TrackTemplateCount,
            header.ClipCount,
            header.BoundaryCount,
            header.TypeCount,
            header.ArenaUnits * Format.PayloadUnit);

        return new DatabaseSections(
            SectionPtr<TimelineHeader>(span, layout.Timelines, header.TimelineCount), header.TimelineCount,
            SectionPtr<TimelineLookupEntry>(span, layout.Lookup, header.TimelineCount), header.TimelineCount,
            SectionPtr<TrackInstance>(span, layout.Tracks, header.TrackCount), header.TrackCount,
            SectionPtr<TrackTemplate>(span, layout.TrackTemplate, header.TrackTemplateCount), header.TrackTemplateCount,
            SectionPtr<ClipEntry>(span, layout.Clips, header.ClipCount), header.ClipCount,
            SectionPtr<TrackBoundary>(span, layout.Boundaries, header.BoundaryCount), header.BoundaryCount,
            SectionPtr<TypeDescriptor>(span, layout.Types, header.TypeCount), header.TypeCount,
            SectionPtr<TrackSpan>(span, layout.Directory, checked(header.TimelineCount * header.TypeCount)),
            checked(header.TimelineCount * header.TypeCount),
            SectionPtr<byte>(span, layout.Arena, header.ArenaUnits * Format.PayloadUnit),
            header.ArenaUnits * Format.PayloadUnit);
    }

    private static nint SectionPtr<T>(ReadOnlySpan<byte> blob, int offset, int count) where T : unmanaged
    {
        return (nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(
            MemoryMarshal.Cast<byte, T>(blob.Slice(offset, checked(count * Unsafe.SizeOf<T>())))));
    }
}

internal readonly record struct SectionLayout(
    int Timelines,
    int Lookup,
    int Tracks,
    int TrackTemplate,
    int Clips,
    int Boundaries,
    int Types,
    int Directory,
    int Arena,
    int TotalBytes)
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static SectionLayout Compute(
        int timelineCount,
        int trackCount,
        int trackDataCount,
        int clipCount,
        int boundaryCount,
        int typeCount,
        int arenaBytes)
    {
        var offset = Unsafe.SizeOf<BlobHeader>();
        var timelines = Advance<TimelineHeader>(ref offset, timelineCount);
        var lookup = Advance<TimelineLookupEntry>(ref offset, timelineCount);
        var tracks = Advance<TrackInstance>(ref offset, trackCount);
        var trackData = Advance<TrackTemplate>(ref offset, trackDataCount);
        var clips = Advance<ClipEntry>(ref offset, clipCount);
        var boundaries = Advance<TrackBoundary>(ref offset, boundaryCount);
        var types = Advance<TypeDescriptor>(ref offset, typeCount);
        var directory = Advance<TrackSpan>(ref offset, checked(timelineCount * typeCount));
        var arena = AdvanceBytes(ref offset, arenaBytes);

        return new SectionLayout(
            timelines,
            lookup,
            tracks,
            trackData,
            clips,
            boundaries,
            types,
            directory,
            arena,
            offset);
    }

    private static int Advance<T>(ref int offset, int count)
        where T : unmanaged
    {
        return AdvanceBytes(ref offset, checked(count * Unsafe.SizeOf<T>()));
    }

    private static int AdvanceBytes(ref int offset, int bytes)
    {
        offset = checked((offset + 7) & ~7);
        var start = offset;
        offset = checked(offset + bytes);
        return start;
    }
}

public sealed class TimelineDatabase
{
    private const uint Magic = 0x49555451;
    private const ushort Version = 2;

    private readonly byte[] _blob;
    private readonly DatabaseSections _sections;

    private TimelineDatabase(byte[] blob)
    {
        var pinned = GC.AllocateUninitializedArray<byte>(blob.Length, true);
        blob.AsSpan().CopyTo(pinned);
        _blob = pinned;
        _sections = DatabaseSections.Compute(pinned);
    }

    public static TimelineDatabase Load(ReadOnlySpan<byte> blob)
    {
        var owned = blob.ToArray();

        if (!TryValidateBlob(owned, out var error)) throw new InvalidDataException(DescribeError(error));

        return new TimelineDatabase(owned);
    }

    internal static TimelineDatabase Create(byte[] blob)
    {
        if (!TryValidateBlob(blob, out var error))
            throw new InvalidOperationException($"Builder produced an invalid iutq database. {DescribeError(error)}");

        return new TimelineDatabase(blob);
    }

    public byte[] ToArray()
    {
        return (byte[])_blob.Clone();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DatabaseView AsView()
    {
        return new DatabaseView(
            _sections.Timelines,
            _sections.TimelineLookup,
            _sections.Tracks,
            _sections.TrackTemplate,
            _sections.Clips,
            _sections.Boundaries,
            _sections.Types,
            _sections.Directory,
            _sections.Arena);
    }

    public ClipTypeHandle<TClip> Resolve<TClip>(ClipType<TClip> type)
        where TClip : unmanaged
    {
        var view = AsView();

        if (view.TryResolve(type, out var handle))
        {
            return handle;
        }

        Check.TypePresent(type.Key);
        return default;
    }

    internal static byte[] Assemble(
        TimelineHeader[] timelines,
        TimelineLookupEntry[] lookup,
        TrackInstance[] tracks,
        TrackTemplate[] trackData,
        ClipEntry[] clips,
        TrackBoundary[] boundaries,
        TypeDescriptor[] types,
        TrackSpan[] directory,
        byte[] arena)
    {
        var layout = SectionLayout.Compute(
            timelines.Length,
            tracks.Length,
            trackData.Length,
            clips.Length,
            boundaries.Length,
            types.Length,
            arena.Length);

        var blob = new byte[layout.TotalBytes];

        BlobHeader initial = new(
            Magic,
            Version,
            0,
            checked((ushort)timelines.Length),
            checked((ushort)tracks.Length),
            checked((ushort)trackData.Length),
            checked((ushort)clips.Length),
            checked((ushort)boundaries.Length),
            checked((ushort)types.Length),
            checked((ushort)(arena.Length / Format.PayloadUnit)),
            0);

        MemoryMarshal.Write(blob.AsSpan(), in initial);
        WriteSection(blob, layout.Timelines, timelines);
        WriteSection(blob, layout.Lookup, lookup);
        WriteSection(blob, layout.Tracks, tracks);
        WriteSection(blob, layout.TrackTemplate, trackData);
        WriteSection(blob, layout.Clips, clips);
        WriteSection(blob, layout.Boundaries, boundaries);
        WriteSection(blob, layout.Types, types);
        WriteSection(blob, layout.Directory, directory);
        arena.CopyTo(blob, layout.Arena);

        var hash = Fnv1A64.Hash(blob.AsSpan(Unsafe.SizeOf<BlobHeader>()));
        BlobHeader final = new(
            Magic,
            Version,
            0,
            checked((ushort)timelines.Length),
            checked((ushort)tracks.Length),
            checked((ushort)trackData.Length),
            checked((ushort)clips.Length),
            checked((ushort)boundaries.Length),
            checked((ushort)types.Length),
            checked((ushort)(arena.Length / Format.PayloadUnit)),
            (uint)hash);

        MemoryMarshal.Write(blob.AsSpan(), in final);
        return blob;
    }

    private static void WriteSection<T>(byte[] blob, int offset, T[] values)
        where T : unmanaged
    {
        values.AsSpan().CopyTo(MemoryMarshal.Cast<byte, T>(
            blob.AsSpan(offset, checked(values.Length * Unsafe.SizeOf<T>()))));
    }

    private static SectionLayout ComputeLayout(in BlobHeader header)
    {
        return SectionLayout.Compute(
            header.TimelineCount,
            header.TrackCount,
            header.TrackTemplateCount,
            header.ClipCount,
            header.BoundaryCount,
            header.TypeCount,
            header.ArenaUnits * Format.PayloadUnit);
    }

    private static string DescribeError(BlobError error)
    {
        return error switch
        {
            BlobError.Truncated => "iutq blob rejected: IUTQ1001: blob is smaller than the fixed header.",
            BlobError.Header => "iutq blob rejected: IUTQ1002: bad magic, version or negative section counts.",
            BlobError.Length => "iutq blob rejected: IUTQ1003: section layout total does not match the blob length.",
            BlobError.Hash => "iutq blob rejected: IUTQ1004: payload hash mismatch (corrupt or truncated body).",
            BlobError.Structure => "iutq blob rejected: IUTQ1005: structural validation failed.",
            _ => "iutq blob rejected: IUTQ1000: unknown error."
        };
    }

    private static bool TryValidateBlob(byte[] blob, out BlobError error)
    {
        try
        {
            ReadOnlySpan<byte> span = blob;

            if (span.Length < Unsafe.SizeOf<BlobHeader>())
            {
                error = BlobError.Truncated;
                return false;
            }

            var header = MemoryMarshal.Read<BlobHeader>(span);

            if (header.Magic != Magic || header.Version != Version)
            {
                error = BlobError.Header;
                return false;
            }

            var layout = ComputeLayout(in header);

            if (layout.TotalBytes != span.Length)
            {
                error = BlobError.Length;
                return false;
            }

            if ((uint)Fnv1A64.Hash(span[Unsafe.SizeOf<BlobHeader>()..]) != header.PayloadHash)
            {
                error = BlobError.Hash;
                return false;
            }

            if (!TryValidateStructure(span, in header, layout))
            {
                error = BlobError.Structure;
                return false;
            }

            error = BlobError.None;
            return true;
        }
        catch (OverflowException)
        {
            error = BlobError.Length;
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            error = BlobError.Length;
            return false;
        }
        catch (ArgumentException)
        {
            error = BlobError.Length;
            return false;
        }
    }

    private static bool TryValidateStructure(
        ReadOnlySpan<byte> blob,
        in BlobHeader header,
        SectionLayout layout)
    {
        var timelines = MemoryMarshal.Cast<byte, TimelineHeader>(
            blob.Slice(layout.Timelines, checked(header.TimelineCount * Unsafe.SizeOf<TimelineHeader>())));
        var lookup = MemoryMarshal.Cast<byte, TimelineLookupEntry>(
            blob.Slice(layout.Lookup, checked(header.TimelineCount * Unsafe.SizeOf<TimelineLookupEntry>())));
        var tracks = MemoryMarshal.Cast<byte, TrackInstance>(
            blob.Slice(layout.Tracks, checked(header.TrackCount * Unsafe.SizeOf<TrackInstance>())));
        var trackData = MemoryMarshal.Cast<byte, TrackTemplate>(
            blob.Slice(layout.TrackTemplate, checked(header.TrackTemplateCount * Unsafe.SizeOf<TrackTemplate>())));
        var clips = MemoryMarshal.Cast<byte, ClipEntry>(
            blob.Slice(layout.Clips, checked(header.ClipCount * Unsafe.SizeOf<ClipEntry>())));
        var boundaries = MemoryMarshal.Cast<byte, TrackBoundary>(
            blob.Slice(layout.Boundaries, checked(header.BoundaryCount * Unsafe.SizeOf<TrackBoundary>())));
        var types = MemoryMarshal.Cast<byte, TypeDescriptor>(
            blob.Slice(layout.Types, checked(header.TypeCount * Unsafe.SizeOf<TypeDescriptor>())));
        var directory = MemoryMarshal.Cast<byte, TrackSpan>(
            blob.Slice(layout.Directory,
                checked(checked(header.TimelineCount * header.TypeCount) * Unsafe.SizeOf<TrackSpan>())));
        var arena = blob.Slice(layout.Arena, header.ArenaUnits * Format.PayloadUnit);

        if (!ValidateTypes(types) || !ValidateTimelines(timelines) || !ValidateLookup(lookup)) return false;

        var maxEnds = new int[trackData.Length];

        for (var i = 0; i < trackData.Length; i++)
        {
            ref readonly var data = ref trackData[i];

            if ((data.Mode != TrackMode.Exclusive && data.Mode != TrackMode.CrossFade) ||
                !RangeWithin(data.ClipStart, data.ClipCount, clips.Length) ||
                !RangeWithin(data.BoundaryStart, data.BoundaryCount, boundaries.Length) ||
                data.LaneSplit > data.ClipCount ||
                data.TypeSlot >= types.Length)
                return false;

            if (data.Mode == TrackMode.Exclusive && data.LaneSplit != data.ClipCount) return false;

            var window = clips.Slice(data.ClipStart, data.ClipCount);
            var payloadSize = types[data.TypeSlot].Size;

            if (!ValidateLane(window[..data.LaneSplit], payloadSize, arena.Length) ||
                !ValidateLane(window[data.LaneSplit..], payloadSize, arena.Length) ||
                !ValidateBoundaries(
                    boundaries.Slice(data.BoundaryStart, data.BoundaryCount),
                    window,
                    data.LaneSplit,
                    data.Mode))
                return false;

            maxEnds[i] = ComputeMaxEnd(window);
        }

        var trackCursor = 0;

        for (var typeSlot = 0; typeSlot < types.Length; typeSlot++)
        for (var timelineIndex = 0; timelineIndex < timelines.Length; timelineIndex++)
        {
            ref readonly var partition = ref directory[typeSlot * timelines.Length + timelineIndex];
            if (!RangeWithin(partition.TrackStart, partition.TrackCount, tracks.Length)) return false;
            if (partition.TrackCount == 0) continue;
            if (partition.TrackStart != trackCursor) return false;
            
            for (var index = partition.TrackStart; index < partition.TrackStart + partition.TrackCount; index++)
            {
                ref readonly var instance = ref tracks[index];
                if ((uint)instance.TrackTemplateId >= (uint)trackData.Length) return false;
                ref readonly var data = ref trackData[instance.TrackTemplateId];
                if (data.TypeSlot != typeSlot ||
                    maxEnds[instance.TrackTemplateId] > timelines[timelineIndex].Duration)
                    return false;
            }

            trackCursor += partition.TrackCount;
        }

        return trackCursor == tracks.Length;
    }

    private static bool ValidateTypes(ReadOnlySpan<TypeDescriptor> types)
    {
        ulong previous = 0;
        for (var i = 0; i < types.Length; i++)
        {
            ref readonly var type = ref types[i];
            if (type.Key == 0 || type.Size == 0 || (i > 0 && type.Key <= previous)) return false;
            previous = type.Key;
        }
        return true;
    }

    private static bool ValidateTimelines(ReadOnlySpan<TimelineHeader> timelines)
    {
        foreach (ref readonly var timeline in timelines)
            if (timeline.Duration == 0 || ((byte)timeline.Flags & ~(byte)TimelineFlags.Loop) != 0)
                return false;
        return true;
    }

    private static bool ValidateLookup(ReadOnlySpan<TimelineLookupEntry> lookup)
    {
        var seen = new bool[lookup.Length];
        ulong previous = 0;

        for (var i = 0; i < lookup.Length; i++)
        {
            ref readonly var entry = ref lookup[i];

            if (entry.Key == 0 ||
                (uint)entry.TimelineIndex >= (uint)lookup.Length ||
                seen[entry.TimelineIndex] ||
                (i > 0 && entry.Key <= previous)) return false;

            seen[entry.TimelineIndex] = true;
            previous = entry.Key;
        }

        return true;
    }

    private static bool ValidateLane(
        ReadOnlySpan<ClipEntry> clips,
        int payloadSize,
        int arenaLength)
    {
        var previousStart = -1;
        var previousEnd = 0;

        foreach (ref readonly var clip in clips)
        {
            if (clip.End <= clip.Start ||
                clip.Start < previousStart ||
                clip.Start < previousEnd ||
                clip.DataOffset * Format.PayloadUnit > arenaLength - payloadSize ||
                (byte)clip.Ease > (byte)ClipEase.CubicInOut) return false;
            previousStart = clip.Start;
            previousEnd = clip.End;
        }

        return true;
    }

    private static bool ValidateBoundaries(
        ReadOnlySpan<TrackBoundary> actual,
        ReadOnlySpan<ClipEntry> clips,
        int laneSplit,
        TrackMode mode)
    {
        List<TrackBoundary> expected = [];

        for (var i = 0; i < clips.Length; i++)
        {
            ref readonly var clip = ref clips[i];

            if (clip.Duration == 1)
            {
                expected.Add(new TrackBoundary(clip.Start, (ushort)i, TrackBoundary.NoClip, BoundaryKind.ClipInstant));
            }
            else
            {
                expected.Add(new TrackBoundary(clip.Start, (ushort)i, TrackBoundary.NoClip, BoundaryKind.ClipStart));
                expected.Add(new TrackBoundary(
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
                var overlapStart = Math.Max(clipA.Start, clipB.Start);
                var overlapEnd = Math.Min(clipA.End, clipB.End);

                if (overlapStart < overlapEnd)
                {
                    if (overlapEnd - overlapStart == 1)
                    {
                        expected.Add(new TrackBoundary(overlapStart, (ushort)a, (ushort)b, BoundaryKind.BlendInstant));
                    }
                    else
                    {
                        expected.Add(new TrackBoundary(overlapStart, (ushort)a, (ushort)b, BoundaryKind.BlendStart));
                        expected.Add(new TrackBoundary(
                            (ushort)(overlapEnd - 1), (ushort)a, (ushort)b, BoundaryKind.BlendEnd));
                    }
                }

                if (clipA.End <= clipB.End) a++;
                else b++;
            }
        }

        expected.Sort(static (a, b) =>
        {
            var tick = a.Tick.CompareTo(b.Tick);
            if (tick != 0) return tick;

            var kind = a.Kind.CompareTo(b.Kind);
            if (kind != 0) return kind;

            var clipA = a.ClipA.CompareTo(b.ClipA);
            return clipA != 0 ? clipA : a.ClipB.CompareTo(b.ClipB);
        });

        if (expected.Count != actual.Length) return false;

        for (var i = 0; i < actual.Length; i++)
        {
            ref readonly var value = ref actual[i];
            var required = expected[i];

            if (value.Tick != required.Tick ||
                value.ClipA != required.ClipA ||
                value.ClipB != required.ClipB ||
                value.Kind != required.Kind)
                return false;
        }

        return true;
    }

    private static int ComputeMaxEnd(ReadOnlySpan<ClipEntry> clips)
    {
        var max = 0;
        foreach (ref readonly var clip in clips) max = Math.Max(max, clip.End);
        return max;
    }

    private static bool RangeWithin(int start, int count, int length)
    {
        return start >= 0 && count >= 0 && start <= length && count <= length - start;
    }

    private enum BlobError
    {
        None = 0,
        Truncated,
        Header,
        Length,
        Hash,
        Structure
    }
}

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

            if (type.Key < descriptor.Key)
            {
                hi = mid - 1;
            }
            else if (type.Key > descriptor.Key)
            {
                lo = mid + 1;
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