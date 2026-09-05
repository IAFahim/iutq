using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Iutq.Core;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct BlobHeader
{
    public readonly uint Magic;
    public readonly ushort Version;
    public readonly ushort Reserved;
    public readonly int TimelineCount;
    public readonly int TrackCount;
    public readonly int TrackDataCount;
    public readonly int ClipCount;
    public readonly int BoundaryCount;
    public readonly int TypeCount;
    public readonly int ArenaBytes;
    public readonly ulong PayloadHash;

    public BlobHeader(
        uint magic,
        ushort version,
        ushort reserved,
        int timelineCount,
        int trackCount,
        int trackDataCount,
        int clipCount,
        int boundaryCount,
        int typeCount,
        int arenaBytes,
        ulong payloadHash)
    {
        Magic = magic;
        Version = version;
        Reserved = reserved;
        TimelineCount = timelineCount;
        TrackCount = trackCount;
        TrackDataCount = trackDataCount;
        ClipCount = clipCount;
        BoundaryCount = boundaryCount;
        TypeCount = typeCount;
        ArenaBytes = arenaBytes;
        PayloadHash = payloadHash;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe readonly struct CachedSections
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

    private CachedSections(
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

    internal ReadOnlySpan<TrackData> TrackData => new((void*)_trackData, _trackDataCount);

    internal ReadOnlySpan<ClipHeader> Clips => new((void*)_clips, _clipCount);

    internal ReadOnlySpan<BoundaryHeader> Boundaries => new((void*)_boundaries, _boundaryCount);

    internal ReadOnlySpan<TypeDescriptor> Types => new((void*)_types, _typeCount);

    internal ReadOnlySpan<TypePartition> Directory => new((void*)_directory, _directoryCount);

    internal ReadOnlySpan<byte> Arena => new((void*)_arena, _arenaBytes);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static CachedSections Compute(byte[] blob)
    {
        ReadOnlySpan<byte> span = blob;
        BlobHeader header = MemoryMarshal.Read<BlobHeader>(span);
        SectionLayout layout = SectionLayout.Compute(
            header.TimelineCount,
            header.TrackCount,
            header.TrackDataCount,
            header.ClipCount,
            header.BoundaryCount,
            header.TypeCount,
            header.ArenaBytes);

        return new CachedSections(
            SectionPtr<TimelineHeader>(span, layout.Timelines, header.TimelineCount), header.TimelineCount,
            SectionPtr<TimelineLookupEntry>(span, layout.Lookup, header.TimelineCount), header.TimelineCount,
            SectionPtr<TrackInstance>(span, layout.Tracks, header.TrackCount), header.TrackCount,
            SectionPtr<TrackData>(span, layout.TrackData, header.TrackDataCount), header.TrackDataCount,
            SectionPtr<ClipHeader>(span, layout.Clips, header.ClipCount), header.ClipCount,
            SectionPtr<BoundaryHeader>(span, layout.Boundaries, header.BoundaryCount), header.BoundaryCount,
            SectionPtr<TypeDescriptor>(span, layout.Types, header.TypeCount), header.TypeCount,
            SectionPtr<TypePartition>(span, layout.Directory, checked(header.TimelineCount * header.TypeCount)), checked(header.TimelineCount * header.TypeCount),
            SectionPtr<byte>(span, layout.Arena, header.ArenaBytes), header.ArenaBytes);
    }

    private static nint SectionPtr<T>(ReadOnlySpan<byte> blob, int offset, int count)
        where T : unmanaged =>
        (nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(
            MemoryMarshal.Cast<byte, T>(blob.Slice(offset, checked(count * Unsafe.SizeOf<T>())))));
}

internal readonly record struct SectionLayout(
    int Timelines,
    int Lookup,
    int Tracks,
    int TrackData,
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
        int offset = Unsafe.SizeOf<BlobHeader>();
        int timelines = Advance<TimelineHeader>(ref offset, timelineCount);
        int lookup = Advance<TimelineLookupEntry>(ref offset, timelineCount);
        int tracks = Advance<TrackInstance>(ref offset, trackCount);
        int trackData = Advance<TrackData>(ref offset, trackDataCount);
        int clips = Advance<ClipHeader>(ref offset, clipCount);
        int boundaries = Advance<BoundaryHeader>(ref offset, boundaryCount);
        int types = Advance<TypeDescriptor>(ref offset, typeCount);
        int directory = Advance<TypePartition>(ref offset, checked(timelineCount * typeCount));
        int arena = AdvanceBytes(ref offset, arenaBytes);

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
        where T : unmanaged =>
        AdvanceBytes(ref offset, checked(count * Unsafe.SizeOf<T>()));

    private static int AdvanceBytes(ref int offset, int bytes)
    {
        offset = checked((offset + 7) & ~7);
        int start = offset;
        offset = checked(offset + bytes);
        return start;
    }
}

public sealed class TimelineDatabase
{
    private const uint Magic = 0x49555451;
    private const ushort Version = 1;

    private readonly byte[] _blob;
    private readonly CachedSections _sections;

    private TimelineDatabase(byte[] blob)
    {
        byte[] pinned = GC.AllocateUninitializedArray<byte>(blob.Length, pinned: true);
        blob.AsSpan().CopyTo(pinned);
        _blob = pinned;
        _sections = CachedSections.Compute(pinned);
    }

    public static TimelineDatabase Load(ReadOnlySpan<byte> blob)
    {
        byte[] owned = blob.ToArray();

        if (!TryValidateBlob(owned, out BlobError error))
        {
            throw new InvalidDataException(DescribeError(error));
        }

        return new TimelineDatabase(owned);
    }

    internal static TimelineDatabase Create(byte[] blob)
    {
        if (!TryValidateBlob(blob, out BlobError error))
        {
            throw new InvalidOperationException($"Builder produced an invalid iutq database. {DescribeError(error)}");
        }

        return new TimelineDatabase(blob);
    }

    public byte[] ToArray() => (byte[])_blob.Clone();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DatabaseView AsView() => new(
        _sections.Timelines,
        _sections.TimelineLookup,
        _sections.Tracks,
        _sections.TrackData,
        _sections.Clips,
        _sections.Boundaries,
        _sections.Types,
        _sections.Directory,
        _sections.Arena);

    public ClipHandle<TClip> Resolve<TClip>(ClipType<TClip> type)
        where TClip : unmanaged
    {
        DatabaseView view = AsView();

        if (!view.TryResolve(type, out ClipHandle<TClip> handle))
        {
            throw new KeyNotFoundException($"Timeline clip type 0x{type.Key:X16} is not present in this database.");
        }

        return handle;
    }

    internal static byte[] Assemble(
        TimelineHeader[] timelines,
        TimelineLookupEntry[] lookup,
        TrackInstance[] tracks,
        TrackData[] trackData,
        ClipHeader[] clips,
        BoundaryHeader[] boundaries,
        TypeDescriptor[] types,
        TypePartition[] directory,
        byte[] arena)
    {
        SectionLayout layout = SectionLayout.Compute(
            timelines.Length,
            tracks.Length,
            trackData.Length,
            clips.Length,
            boundaries.Length,
            types.Length,
            arena.Length);

        byte[] blob = new byte[layout.TotalBytes];

        BlobHeader initial = new(
            Magic,
            Version,
            0,
            timelines.Length,
            tracks.Length,
            trackData.Length,
            clips.Length,
            boundaries.Length,
            types.Length,
            arena.Length,
            0);

        MemoryMarshal.Write(blob.AsSpan(), in initial);
        WriteSection(blob, layout.Timelines, timelines);
        WriteSection(blob, layout.Lookup, lookup);
        WriteSection(blob, layout.Tracks, tracks);
        WriteSection(blob, layout.TrackData, trackData);
        WriteSection(blob, layout.Clips, clips);
        WriteSection(blob, layout.Boundaries, boundaries);
        WriteSection(blob, layout.Types, types);
        WriteSection(blob, layout.Directory, directory);
        arena.CopyTo(blob, layout.Arena);

        ulong hash = Fnv1a64.Hash(blob.AsSpan(Unsafe.SizeOf<BlobHeader>()));
        BlobHeader final = new(
            Magic,
            Version,
            0,
            timelines.Length,
            tracks.Length,
            trackData.Length,
            clips.Length,
            boundaries.Length,
            types.Length,
            arena.Length,
            hash);

        MemoryMarshal.Write(blob.AsSpan(), in final);
        return blob;
    }

    private static void WriteSection<T>(byte[] blob, int offset, T[] values)
        where T : unmanaged
    {
        values.AsSpan().CopyTo(MemoryMarshal.Cast<byte, T>(
            blob.AsSpan(offset, checked(values.Length * Unsafe.SizeOf<T>()))));
    }

    private static SectionLayout ComputeLayout(in BlobHeader header) =>
        SectionLayout.Compute(
            header.TimelineCount,
            header.TrackCount,
            header.TrackDataCount,
            header.ClipCount,
            header.BoundaryCount,
            header.TypeCount,
            header.ArenaBytes);

    internal enum BlobError
    {
        None = 0,
        Truncated,
        Header,
        Length,
        Hash,
        Structure,
    }

    private static string DescribeError(BlobError error) => error switch
    {
        BlobError.Truncated => "iutq blob rejected: IUTQ1001: blob is smaller than the fixed header.",
        BlobError.Header => "iutq blob rejected: IUTQ1002: bad magic, version or negative section counts.",
        BlobError.Length => "iutq blob rejected: IUTQ1003: section layout total does not match the blob length.",
        BlobError.Hash => "iutq blob rejected: IUTQ1004: payload hash mismatch (corrupt or truncated body).",
        BlobError.Structure => "iutq blob rejected: IUTQ1005: structural validation failed.",
        _ => "iutq blob rejected: IUTQ1000: unknown error.",
    };

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

            BlobHeader header = MemoryMarshal.Read<BlobHeader>(span);

            if (header.Magic != Magic ||
                header.Version != Version ||
                header.TimelineCount < 0 ||
                header.TrackCount < 0 ||
                header.TrackDataCount < 0 ||
                header.ClipCount < 0 ||
                header.BoundaryCount < 0 ||
                header.TypeCount < 0 ||
                header.ArenaBytes < 0)
            {
                error = BlobError.Header;
                return false;
            }

            SectionLayout layout = ComputeLayout(in header);

            if (layout.TotalBytes != span.Length)
            {
                error = BlobError.Length;
                return false;
            }

            if (Fnv1a64.Hash(span[Unsafe.SizeOf<BlobHeader>()..]) != header.PayloadHash)
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
        ReadOnlySpan<TimelineHeader> timelines = MemoryMarshal.Cast<byte, TimelineHeader>(
            blob.Slice(layout.Timelines, checked(header.TimelineCount * Unsafe.SizeOf<TimelineHeader>())));
        ReadOnlySpan<TimelineLookupEntry> lookup = MemoryMarshal.Cast<byte, TimelineLookupEntry>(
            blob.Slice(layout.Lookup, checked(header.TimelineCount * Unsafe.SizeOf<TimelineLookupEntry>())));
        ReadOnlySpan<TrackInstance> tracks = MemoryMarshal.Cast<byte, TrackInstance>(
            blob.Slice(layout.Tracks, checked(header.TrackCount * Unsafe.SizeOf<TrackInstance>())));
        ReadOnlySpan<TrackData> trackData = MemoryMarshal.Cast<byte, TrackData>(
            blob.Slice(layout.TrackData, checked(header.TrackDataCount * Unsafe.SizeOf<TrackData>())));
        ReadOnlySpan<ClipHeader> clips = MemoryMarshal.Cast<byte, ClipHeader>(
            blob.Slice(layout.Clips, checked(header.ClipCount * Unsafe.SizeOf<ClipHeader>())));
        ReadOnlySpan<BoundaryHeader> boundaries = MemoryMarshal.Cast<byte, BoundaryHeader>(
            blob.Slice(layout.Boundaries, checked(header.BoundaryCount * Unsafe.SizeOf<BoundaryHeader>())));
        ReadOnlySpan<TypeDescriptor> types = MemoryMarshal.Cast<byte, TypeDescriptor>(
            blob.Slice(layout.Types, checked(header.TypeCount * Unsafe.SizeOf<TypeDescriptor>())));
        ReadOnlySpan<TypePartition> directory = MemoryMarshal.Cast<byte, TypePartition>(
            blob.Slice(layout.Directory, checked(checked(header.TimelineCount * header.TypeCount) * Unsafe.SizeOf<TypePartition>())));
        ReadOnlySpan<byte> arena = blob.Slice(layout.Arena, header.ArenaBytes);

        if (!ValidateTypes(types) || !ValidateTimelines(timelines) || !ValidateLookup(timelines, lookup))
        {
            return false;
        }

        int[] maxEnds = new int[trackData.Length];

        for (int i = 0; i < trackData.Length; i++)
        {
            ref readonly TrackData data = ref trackData[i];

            if ((data.Mode != TrackMode.Exclusive && data.Mode != TrackMode.CrossFade) ||
                !RangeWithin(data.ClipStart, data.ClipCount, clips.Length) ||
                !RangeWithin(data.BoundaryStart, data.BoundaryCount, boundaries.Length) ||
                data.LaneSplit < 0 ||
                data.LaneSplit > data.ClipCount ||
                !TryFindType(types, data.TypeKey, out int typeSlot))
            {
                return false;
            }

            if (data.Mode == TrackMode.Exclusive && data.LaneSplit != data.ClipCount)
            {
                return false;
            }

            ReadOnlySpan<ClipHeader> window = clips.Slice(data.ClipStart, data.ClipCount);

            if (!ValidateLane(window[..data.LaneSplit], types[typeSlot].Size, arena.Length) ||
                !ValidateLane(window[data.LaneSplit..], types[typeSlot].Size, arena.Length) ||
                !ValidateBoundaries(
                    boundaries.Slice(data.BoundaryStart, data.BoundaryCount),
                    window,
                    data.LaneSplit,
                    data.Mode))
            {
                return false;
            }

            maxEnds[i] = ComputeMaxEnd(window);
        }

        int trackCursor = 0;

        for (int typeSlot = 0; typeSlot < types.Length; typeSlot++)
        {
            for (int timelineId = 0; timelineId < timelines.Length; timelineId++)
            {
                ref readonly TypePartition partition = ref directory[typeSlot * timelines.Length + timelineId];

                if (!RangeWithin(partition.TrackStart, partition.TrackCount, tracks.Length))
                {
                    return false;
                }

                if (partition.TrackCount == 0)
                {
                    continue;
                }

                if (partition.TrackStart != trackCursor)
                {
                    return false;
                }

                for (int trackIndex = partition.TrackStart;
                     trackIndex < partition.TrackStart + partition.TrackCount;
                     trackIndex++)
                {
                    ref readonly TrackInstance instance = ref tracks[trackIndex];

                    if ((uint)instance.TrackDataId >= (uint)trackData.Length)
                    {
                        return false;
                    }

                    ref readonly TrackData data = ref trackData[instance.TrackDataId];

                    if (data.TypeKey != types[typeSlot].Key ||
                        maxEnds[instance.TrackDataId] > timelines[timelineId].Duration)
                    {
                        return false;
                    }
                }

                trackCursor += partition.TrackCount;
            }
        }

        return trackCursor == tracks.Length;
    }

    private static bool ValidateTypes(ReadOnlySpan<TypeDescriptor> types)
    {
        ulong previous = 0;

        for (int i = 0; i < types.Length; i++)
        {
            ref readonly TypeDescriptor type = ref types[i];

            if (type.Key == 0 ||
                type.Size <= 0 ||
                (i > 0 && type.Key <= previous))
            {
                return false;
            }

            previous = type.Key;
        }

        return true;
    }

    private static bool ValidateTimelines(ReadOnlySpan<TimelineHeader> timelines)
    {
        foreach (ref readonly TimelineHeader timeline in timelines)
        {
            if (timeline.Key == 0 ||
                timeline.Duration <= 0 ||
                ((byte)timeline.Flags & ~(byte)TimelineFlags.Loop) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidateLookup(
        ReadOnlySpan<TimelineHeader> timelines,
        ReadOnlySpan<TimelineLookupEntry> lookup)
    {
        ulong previous = 0;

        for (int i = 0; i < lookup.Length; i++)
        {
            ref readonly TimelineLookupEntry entry = ref lookup[i];

            if ((uint)entry.TimelineId >= (uint)timelines.Length ||
                timelines[entry.TimelineId].Key != entry.Key ||
                (i > 0 && entry.Key <= previous))
            {
                return false;
            }

            previous = entry.Key;
        }

        return true;
    }

    private static bool ValidateLane(
        ReadOnlySpan<ClipHeader> clips,
        int payloadSize,
        int arenaLength)
    {
        int previousStart = -1;
        int previousEnd = 0;

        foreach (ref readonly ClipHeader clip in clips)
        {
            if (clip.Start < 0 ||
                clip.End <= clip.Start ||
                clip.Start < previousStart ||
                clip.Start < previousEnd ||
                clip.DataOffset < 0 ||
                clip.DataOffset > arenaLength - payloadSize ||
                (byte)clip.Ease > (byte)ClipEase.CubicInOut)
            {
                return false;
            }

            previousStart = clip.Start;
            previousEnd = clip.End;
        }

        return true;
    }

    private static bool ValidateBoundaries(
        ReadOnlySpan<BoundaryHeader> actual,
        ReadOnlySpan<ClipHeader> clips,
        int laneSplit,
        TrackMode mode)
    {
        List<BoundaryHeader> expected = [];

        for (int i = 0; i < clips.Length; i++)
        {
            ref readonly ClipHeader clip = ref clips[i];

            if (clip.Duration == 1)
            {
                expected.Add(new BoundaryHeader(clip.Start, i, -1, BoundaryKind.ClipSingle));
            }
            else
            {
                expected.Add(new BoundaryHeader(clip.Start, i, -1, BoundaryKind.ClipLeft));
                expected.Add(new BoundaryHeader(clip.End - 1, i, -1, BoundaryKind.ClipRight));
            }
        }

        if (mode == TrackMode.CrossFade && laneSplit < clips.Length)
        {
            int a = 0;
            int b = laneSplit;

            while (a < laneSplit && b < clips.Length)
            {
                ref readonly ClipHeader clipA = ref clips[a];
                ref readonly ClipHeader clipB = ref clips[b];
                int overlapStart = Math.Max(clipA.Start, clipB.Start);
                int overlapEnd = Math.Min(clipA.End, clipB.End);

                if (overlapStart < overlapEnd)
                {
                    if (overlapEnd - overlapStart == 1)
                    {
                        expected.Add(new BoundaryHeader(
                            overlapStart,
                            a,
                            b,
                            BoundaryKind.BlendSingle));
                    }
                    else
                    {
                        expected.Add(new BoundaryHeader(
                            overlapStart,
                            a,
                            b,
                            BoundaryKind.BlendLeft));
                        expected.Add(new BoundaryHeader(
                            overlapEnd - 1,
                            a,
                            b,
                            BoundaryKind.BlendRight));
                    }
                }

                if (clipA.End <= clipB.End)
                {
                    a++;
                }
                else
                {
                    b++;
                }
            }
        }

        expected.Sort(static (a, b) =>
        {
            int tick = a.Tick.CompareTo(b.Tick);
            if (tick != 0) return tick;

            int kind = a.Kind.CompareTo(b.Kind);
            if (kind != 0) return kind;

            int clipA = a.ClipA.CompareTo(b.ClipA);
            return clipA != 0 ? clipA : a.ClipB.CompareTo(b.ClipB);
        });

        if (expected.Count != actual.Length)
        {
            return false;
        }

        for (int i = 0; i < actual.Length; i++)
        {
            ref readonly BoundaryHeader value = ref actual[i];
            BoundaryHeader required = expected[i];

            if (value.Tick != required.Tick ||
                value.ClipA != required.ClipA ||
                value.ClipB != required.ClipB ||
                value.Kind != required.Kind)
            {
                return false;
            }
        }

        return true;
    }

    private static int ComputeMaxEnd(ReadOnlySpan<ClipHeader> clips)
    {
        int max = 0;

        foreach (ref readonly ClipHeader clip in clips)
        {
            max = Math.Max(max, clip.End);
        }

        return max;
    }

    private static bool TryFindType(ReadOnlySpan<TypeDescriptor> types, ulong key, out int slot)
    {
        int lo = 0;
        int hi = types.Length - 1;

        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            ulong candidate = types[mid].Key;

            if (key < candidate)
            {
                hi = mid - 1;
            }
            else if (key > candidate)
            {
                lo = mid + 1;
            }
            else
            {
                slot = mid;
                return true;
            }
        }

        slot = -1;
        return false;
    }

    private static bool RangeWithin(int start, int count, int length) =>
        start >= 0 &&
        count >= 0 &&
        start <= length &&
        count <= length - start;
}

public readonly ref struct DatabaseView
{
    public readonly ReadOnlySpan<TimelineHeader> Timelines;
    public readonly ReadOnlySpan<TimelineLookupEntry> TimelineLookup;
    public readonly ReadOnlySpan<TrackInstance> Tracks;
    public readonly ReadOnlySpan<TrackData> TrackData;
    public readonly ReadOnlySpan<ClipHeader> Clips;
    internal readonly ReadOnlySpan<BoundaryHeader> Boundaries;
    public readonly ReadOnlySpan<TypeDescriptor> Types;
    public readonly ReadOnlySpan<TypePartition> Directory;
    public readonly ReadOnlySpan<byte> Arena;

    internal DatabaseView(
        ReadOnlySpan<TimelineHeader> timelines,
        ReadOnlySpan<TimelineLookupEntry> timelineLookup,
        ReadOnlySpan<TrackInstance> tracks,
        ReadOnlySpan<TrackData> trackData,
        ReadOnlySpan<ClipHeader> clips,
        ReadOnlySpan<BoundaryHeader> boundaries,
        ReadOnlySpan<TypeDescriptor> types,
        ReadOnlySpan<TypePartition> directory,
        ReadOnlySpan<byte> arena)
    {
        Timelines = timelines;
        TimelineLookup = timelineLookup;
        Tracks = tracks;
        TrackData = trackData;
        Clips = clips;
        Boundaries = boundaries;
        Types = types;
        Directory = directory;
        Arena = arena;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryResolve(TimelineKey key, out TimelineId timelineId)
    {
        int lo = 0;
        int hi = TimelineLookup.Length - 1;

        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            ref readonly TimelineLookupEntry entry = ref TimelineLookup[mid];

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
                timelineId = new TimelineId(entry.TimelineId);
                return true;
            }
        }

        timelineId = TimelineId.None;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryResolve<TClip>(ClipType<TClip> type, out ClipHandle<TClip> handle)
        where TClip : unmanaged
    {
        int lo = 0;
        int hi = Types.Length - 1;

        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            ref readonly TypeDescriptor descriptor = ref Types[mid];

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
                handle = new ClipHandle<TClip>(mid);
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
    public ClipQuery<TClip> Query<TClip>(ClipHandle<TClip> handle)
        where TClip : unmanaged
    {
        if (!handle.IsValid || (uint)handle.TypeSlot >= (uint)Types.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(handle));
        }

        return new ClipQuery<TClip>(this, handle.TypeSlot);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetTimeline(TimelineId timelineId, out TimelineHeader timeline)
    {
        if ((uint)timelineId.Value >= (uint)Timelines.Length)
        {
            timeline = default;
            return false;
        }

        timeline = Unsafe.Add(ref MemoryMarshal.GetReference(Timelines), (nint)(uint)timelineId.Value);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly T Payload<T>(int dataOffset)
        where T : unmanaged
    {
        ref byte arena = ref MemoryMarshal.GetReference(Arena);
        return ref Unsafe.As<byte, T>(ref Unsafe.Add(ref arena, dataOffset));
    }
}
