namespace Iutq.Waffle.Generator;

/// <summary>
///     Parser for the iutq blob v2 binary format (the authoritative layout emitted by
///     Iutq.Core's TimelineDatabase.Assemble):
///     28-byte header, then 8-aligned sections: TimelineHeader(4) xN, TimelineLookupEntry(12) xN,
///     TrackInstance(4) xM, TrackTemplate(14) xK, ClipEntry(8) xC, TrackBoundary(8) xB,
///     TypeDescriptor(12) xY, TrackSpan(4) x(Y*N), Arena(4*ArenaUnits bytes).
/// </summary>
internal static class BlobParser
{
    public const uint Magic = 0x49555451U;
    public const ushort Version = 2;
    public const int HeaderSize = 28;
    public const int PayloadUnit = 4;

    public const byte KindClipStart = 0;
    public const byte KindBlendStart = 1;
    public const byte KindClipInstant = 2;
    public const byte KindBlendInstant = 3;
    public const byte KindBlendEnd = 4;
    public const byte KindClipEnd = 5;

    public const ushort NoClip = 0xFFFF;

    /// <summary>Keep the merged per-partition event stream storable in a ushort prefix table.</summary>
    public const int MaxMergedEvents = 65000;

    public static SourceInfo Parse(string path, byte[] blob)
    {
        if (blob.Length < HeaderSize)
            throw new InvalidDataException(
                $"iutq blob '{path}' rejected: {blob.Length} bytes is smaller than the {HeaderSize}-byte header.");

        if (ReadU32(blob, 0) != Magic)
            throw new InvalidDataException($"iutq blob '{path}' rejected: bad magic.");

        if (ReadU16(blob, 4) != Version)
            throw new InvalidDataException(
                $"iutq blob '{path}' rejected: unsupported version {ReadU16(blob, 4)} (expected {Version}).");

        var timelineCount = ReadU16(blob, 8);
        var trackCount = ReadU16(blob, 10);
        var templateCount = ReadU16(blob, 12);
        var clipCount = ReadU16(blob, 14);
        var boundaryCount = ReadU16(blob, 16);
        var typeCount = ReadU16(blob, 18);
        var arenaUnits = ReadU16(blob, 20);
        var payloadHash = ReadU32(blob, 24);

        var offset = HeaderSize;
        var timelinesOff = TakeSection(ref offset, timelineCount * 4);
        var lookupOff = TakeSection(ref offset, timelineCount * 12);
        var tracksOff = TakeSection(ref offset, trackCount * 4);
        var templatesOff = TakeSection(ref offset, templateCount * 14);
        var clipsOff = TakeSection(ref offset, clipCount * 8);
        var boundariesOff = TakeSection(ref offset, boundaryCount * 8);
        var typesOff = TakeSection(ref offset, typeCount * 12);
        var directoryOff = TakeSection(ref offset, checked(typeCount * timelineCount * 4));
        var arenaOff = TakeSection(ref offset, arenaUnits * PayloadUnit);

        if (offset != blob.Length)
            throw new InvalidDataException(
                $"iutq blob '{path}' rejected: section layout ends at {offset} but the blob is {blob.Length} bytes.");

        var source = new SourceInfo
        {
            OriginalName = Path.GetFileNameWithoutExtension(path),
            SanitizedName = Sanitize(Path.GetFileNameWithoutExtension(path)),
            Base64 = Convert.ToBase64String(blob),
            ArenaOffset = arenaOff,
            ArenaUnits = arenaUnits,
            PayloadHash = payloadHash
        };

        // Timeline headers: ushort Duration, byte Flags (bit0 = Loop), pad.
        var timelineDurations = new int[timelineCount];
        var timelineLoops = new bool[timelineCount];
        var timelineKeys = new ulong[timelineCount];

        for (var i = 0; i < timelineCount; i++)
        {
            timelineDurations[i] = ReadU16(blob, timelinesOff + i * 4);
            timelineLoops[i] = (blob[timelinesOff + i * 4 + 2] & 1) != 0;
        }

        // Lookup is sorted by key; map timelineIndex -> key.
        for (var i = 0; i < timelineCount; i++)
        {
            var key = ReadU64(blob, lookupOff + i * 12);
            var index = ReadU16(blob, lookupOff + i * 12 + 8);
            timelineKeys[index] = key;
        }

        var typeKeys = new ulong[typeCount];
        var typeSizes = new int[typeCount];

        for (var i = 0; i < typeCount; i++)
        {
            typeKeys[i] = ReadU64(blob, typesOff + i * 12);
            typeSizes[i] = ReadU16(blob, typesOff + i * 12 + 8);
        }

        var clipStarts = new int[clipCount];
        var clipEnds = new int[clipCount];
        var clipOffsets = new int[clipCount];

        for (var i = 0; i < clipCount; i++)
        {
            clipStarts[i] = ReadU16(blob, clipsOff + i * 8);
            clipEnds[i] = ReadU16(blob, clipsOff + i * 8 + 2);
            clipOffsets[i] = ReadU16(blob, clipsOff + i * 8 + 4);
        }

        for (var typeSlot = 0; typeSlot < typeCount; typeSlot++)
        for (var timelineIndex = 0; timelineIndex < timelineCount; timelineIndex++)
        {
            var directoryIndex = typeSlot * timelineCount + timelineIndex;
            var trackStart = ReadU16(blob, directoryOff + directoryIndex * 4);
            var partitionTrackCount = ReadU16(blob, directoryOff + directoryIndex * 4 + 2);

            if (partitionTrackCount == 0)
                continue;

            var partition = new PartitionInfo
            {
                TimelineIndex = timelineIndex,
                TypeSlot = typeSlot,
                TimelineKey = timelineKeys[timelineIndex],
                Duration = timelineDurations[timelineIndex],
                Loop = timelineLoops[timelineIndex],
                TypeKey = typeKeys[typeSlot],
                PayloadSize = typeSizes[typeSlot],
                ClassName = typeCount == 1 ? $"Timeline{timelineIndex}" : $"Timeline{timelineIndex}_Type{typeSlot}"
            };

            for (var ordinal = 0; ordinal < partitionTrackCount; ordinal++)
            {
                var trackIndex = trackStart + ordinal;
                var binding = ReadU16(blob, tracksOff + trackIndex * 4);
                var templateId = ReadU16(blob, tracksOff + trackIndex * 4 + 2);

                var clipStart = ReadU16(blob, templatesOff + templateId * 14 + 2);
                var clipCountInTemplate = ReadU16(blob, templatesOff + templateId * 14 + 4);
                var laneSplit = ReadU16(blob, templatesOff + templateId * 14 + 6);
                var boundaryStart = ReadU16(blob, templatesOff + templateId * 14 + 8);
                var boundaryCountInTemplate = ReadU16(blob, templatesOff + templateId * 14 + 10);
                var mode = blob[templatesOff + templateId * 14 + 12];

                var track = new TrackInfo
                {
                    Index = ordinal,
                    Binding = binding,
                    TemplateId = templateId,
                    CrossFade = mode == 1
                };

                for (var c = 0; c < clipCountInTemplate; c++)
                {
                    var clipIndex = clipStart + c;
                    var info = new ClipInfo
                    {
                        Start = clipStarts[clipIndex],
                        Len = clipEnds[clipIndex] - clipStarts[clipIndex],
                        Offset = clipOffsets[clipIndex]
                    };

                    if (c < laneSplit)
                        track.LaneA.Add(info);
                    else
                        track.LaneB.Add(info);
                }

                if (track.CrossFade)
                    CollectBlendPairs(track);

                track.Strategy = StrategySelector.Select(track, partition.Duration);
                partition.Tracks.Add(track);

                CollectEvents(
                    blob,
                    boundariesOff,
                    boundaryStart,
                    boundaryCountInTemplate,
                    clipStarts,
                    clipEnds,
                    clipOffsets,
                    clipStart,
                    ordinal,
                    partition);
            }

            FinalizePartition(partition);
            source.Partitions.Add(partition);
        }

        return source;
    }

    /// <summary>Replicates the builder's crossfade overlap scan to find active (A, B) pairs.</summary>
    private static void CollectBlendPairs(TrackInfo track)
    {
        var a = 0;
        var b = 0;

        while (a < track.LaneA.Count && b < track.LaneB.Count)
        {
            var clipA = track.LaneA[a];
            var clipB = track.LaneB[b];
            var blendStart = Math.Max(clipA.Start, clipB.Start);
            var blendEnd = Math.Min(clipA.Start + clipA.Len, clipB.Start + clipB.Len);

            if (blendStart < blendEnd)
            {
                track.Pairs.Add(new BlendPairInfo
                {
                    LaneAIndex = a,
                    LaneBIndex = b,
                    BlendStart = blendStart,
                    BlendEnd = blendEnd,
                    OffsetA = clipA.Offset,
                    OffsetB = clipB.Offset,
                    LaneBFirst = clipB.Start >= clipA.Start
                });
            }

            if (clipA.Start + clipA.Len <= clipB.Start + clipB.Len)
                a++;
            else
                b++;
        }
    }

    private static void CollectEvents(
        byte[] blob,
        int boundariesOff,
        int boundaryStart,
        int boundaryCount,
        int[] clipStarts,
        int[] clipEnds,
        int[] clipOffsets,
        int templateClipStart,
        int trackOrdinal,
        PartitionInfo partition)
    {
        for (var i = 0; i < boundaryCount; i++)
        {
            var at = boundariesOff + (boundaryStart + i) * 8;
            var tick = ReadU16(blob, at);
            var clipA = ReadU16(blob, at + 2);
            var clipB = ReadU16(blob, at + 4);
            var kind = blob[at + 6];

            var isBlend = kind is KindBlendStart or KindBlendInstant or KindBlendEnd;
            var factorText = "0f";

            if (isBlend)
            {
                // Replicate TimelineMath.BlendFactor(tick, max(startA, startB), min(endA, endB)).
                var blendStart = Math.Max(clipStarts[templateClipStart + clipA], clipStarts[templateClipStart + clipB]);
                var blendEnd = Math.Min(clipEnds[templateClipStart + clipA], clipEnds[templateClipStart + clipB]);
                factorText = BlendFactorLiteral(tick, blendStart, blendEnd);
            }

            partition.AddEvent(
                trackOrdinal,
                tick,
                clipOffsets[templateClipStart + clipA],
                isBlend ? clipOffsets[templateClipStart + clipB] : NoClip,
                kind,
                factorText);
        }
    }

    /// <summary>Float literal replicating TimelineMath.BlendFactor(tick, start, end).</summary>
    public static string BlendFactorLiteral(int tick, int start, int end)
    {
        var duration = end - start;
        var factor = duration <= 1 ? 0.5f : (float)(tick - start) / (duration - 1);
        return FormatFloat(factor);
    }

    /// <summary>Round-trippable float literal ("R" + 'f' suffix).</summary>
    public static string FormatFloat(float value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f";

    private static void FinalizePartition(PartitionInfo partition)
    {
        partition.FreezeEvents();

        // Stable sort by (tick, track ordinal): per-track boundary order is preserved for
        // equal keys, matching the runtime's per-track emission at the same tick.
        var order = Enumerable.Range(0, partition.EventCount).ToArray();
        var ticks = partition.EvTick;
        var tracks = partition.EvTrack;
        var kinds = partition.EvKind;
        var clipsA = partition.EvOffA;
        var clipsB = partition.EvOffB;

        Array.Sort(order, (x, y) =>
        {
            var byTick = ticks[x].CompareTo(ticks[y]);
            if (byTick != 0)
                return byTick;

            var byTrack = tracks[x].CompareTo(tracks[y]);
            if (byTrack != 0)
                return byTrack;

            var byKind = kinds[x].CompareTo(kinds[y]);
            if (byKind != 0)
                return byKind;

            var byA = clipsA[x].CompareTo(clipsA[y]);
            return byA != 0 ? byA : clipsB[x].CompareTo(clipsB[y]);
        });

        partition.EvTrack = order.Select(i => tracks[i]).ToArray();
        partition.EvTick = order.Select(i => ticks[i]).ToArray();
        partition.EvOffA = order.Select(i => clipsA[i]).ToArray();
        partition.EvOffB = order.Select(i => clipsB[i]).ToArray();
        partition.EvKind = order.Select(i => kinds[i]).ToArray();
        partition.EvFactor = order.Select(i => partition.EvFactor[i]).ToArray();

        if (partition.EventCount > MaxMergedEvents)
            throw new InvalidDataException(
                $"Partition {partition.ClassName} has {partition.EventCount} merged transition events; " +
                $"the prefix table cap is {MaxMergedEvents}.");

        // Prefix table: TickStart[t] = first event index with Tick >= t; TickStart[duration] = count.
        var tickStart = new ushort[partition.Duration + 1];
        var eventIndex = 0;

        for (var t = 0; t <= partition.Duration; t++)
        {
            while (eventIndex < partition.EventCount && partition.EvTick[eventIndex] < t)
                eventIndex++;

            tickStart[t] = (ushort)eventIndex;
        }

        partition.TickStart = tickStart;
    }

    private static int TakeSection(ref int offset, int bytes)
    {
        offset = (offset + 7) & ~7;
        var start = offset;
        offset = checked(offset + bytes);
        return start;
    }

    private static ushort ReadU16(byte[] blob, int offset) => (ushort)(blob[offset] | (blob[offset + 1] << 8));

    private static uint ReadU32(byte[] blob, int offset) =>
        (uint)(blob[offset] | (blob[offset + 1] << 8) | (blob[offset + 2] << 16) | (blob[offset + 3] << 24));

    private static ulong ReadU64(byte[] blob, int offset) =>
        ReadU32(blob, offset) | ((ulong)ReadU32(blob, offset + 4) << 32);

    public static string Sanitize(string stem)
    {
        var chars = stem.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();

        if (chars.Length == 0)
            return "Blob";

        if (char.IsDigit(chars[0]))
            return "_" + new string(chars);

        return new string(chars);
    }
}
