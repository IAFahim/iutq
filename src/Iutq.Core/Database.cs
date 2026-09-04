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

    private TimelineDatabase(byte[] blob) => _blob = blob;

    public static TimelineDatabase Load(ReadOnlySpan<byte> blob)
    {
        byte[] owned = blob.ToArray();

        if (!TryValidateBlob(owned))
        {
            throw new InvalidDataException("iutq database blob is invalid.");
        }

        return new TimelineDatabase(owned);
    }

    internal static TimelineDatabase Create(byte[] blob)
    {
        if (!TryValidateBlob(blob))
        {
            throw new InvalidOperationException("Builder produced an invalid iutq database.");
        }

        return new TimelineDatabase(blob);
    }

    public byte[] ToArray() => (byte[])_blob.Clone();

    public DatabaseView AsView()
    {
        ReadOnlySpan<byte> blob = _blob;
        BlobHeader header = MemoryMarshal.Read<BlobHeader>(blob);
        SectionLayout layout = ComputeLayout(in header);

        return new DatabaseView(
            MemoryMarshal.Cast<byte, TimelineHeader>(blob.Slice(layout.Timelines, checked(header.TimelineCount * Unsafe.SizeOf<TimelineHeader>()))),
            MemoryMarshal.Cast<byte, TimelineLookupEntry>(blob.Slice(layout.Lookup, checked(header.TimelineCount * Unsafe.SizeOf<TimelineLookupEntry>()))),
            MemoryMarshal.Cast<byte, TrackInstance>(blob.Slice(layout.Tracks, checked(header.TrackCount * Unsafe.SizeOf<TrackInstance>()))),
            MemoryMarshal.Cast<byte, TrackData>(blob.Slice(layout.TrackData, checked(header.TrackDataCount * Unsafe.SizeOf<TrackData>()))),
            MemoryMarshal.Cast<byte, ClipHeader>(blob.Slice(layout.Clips, checked(header.ClipCount * Unsafe.SizeOf<ClipHeader>()))),
            MemoryMarshal.Cast<byte, BoundaryHeader>(blob.Slice(layout.Boundaries, checked(header.BoundaryCount * Unsafe.SizeOf<BoundaryHeader>()))),
            MemoryMarshal.Cast<byte, TypeDescriptor>(blob.Slice(layout.Types, checked(header.TypeCount * Unsafe.SizeOf<TypeDescriptor>()))),
            MemoryMarshal.Cast<byte, TypePartition>(blob.Slice(layout.Directory, checked(checked(header.TimelineCount * header.TypeCount) * Unsafe.SizeOf<TypePartition>()))),
            blob.Slice(layout.Arena, header.ArenaBytes));
    }

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

    private static bool TryValidateBlob(byte[] blob)
    {
        try
        {
            ReadOnlySpan<byte> span = blob;

            if (span.Length < Unsafe.SizeOf<BlobHeader>())
            {
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
                return false;
            }

            SectionLayout layout = ComputeLayout(in header);

            if (layout.TotalBytes != span.Length ||
                Fnv1a64.Hash(span[Unsafe.SizeOf<BlobHeader>()..]) != header.PayloadHash)
            {
                return false;
            }

            return TryValidateStructure(span, in header, layout);
        }
        catch (OverflowException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (ArgumentException)
        {
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

        timeline = Timelines[timelineId.Value];
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly T Payload<T>(int dataOffset)
        where T : unmanaged =>
        ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(
            Arena.Slice(dataOffset, Unsafe.SizeOf<T>())));
}
