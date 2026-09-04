using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Iutq.Core;

#pragma warning disable CA1711
public enum TimelineFlags : byte
{
    None = 0,
    Loop = 1,
}
#pragma warning restore CA1711

public enum TrackMode : byte
{
    Exclusive = 0,
    CrossFade = 1,
}

public enum ClipEase : byte
{
    Linear = 0,
    QuadIn = 1,
    QuadOut = 2,
    CubicInOut = 3,
}

public enum TimelineDirection : sbyte
{
    Reverse = -1,
    None = 0,
    Forward = 1,
}

public enum ClipPhase : byte
{
    None = 0,
    Enter = 1,
    Stay = 2,
    Exit = 3,
}

public enum BlendPhase : byte
{
    None = 0,
    Enter = 1,
    Stay = 2,
    Exit = 3,
}

internal enum BoundaryKind : byte
{
    ClipLeft = 0,
    BlendLeft = 1,
    ClipSingle = 2,
    BlendSingle = 3,
    BlendRight = 4,
    ClipRight = 5,
}

public readonly record struct TimelineKey(ulong Value);

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TimelineId
{
    public readonly int Value;

    public TimelineId(int value) => Value = value;

    public static readonly TimelineId None = new(-1);

    public bool IsValid => Value >= 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct BindingId
{
    public readonly int Value;

    public BindingId(int value) => Value = value;

    public static readonly BindingId None = new(-1);
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly struct ClipType<T>
    where T : unmanaged
{
    public readonly ulong Key;

    public ClipType(ulong key)
    {
        ArgumentOutOfRangeException.ThrowIfZero(key);

        Key = key;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct ClipHandle<T>
    where T : unmanaged
{
    private readonly int _slotPlusOne;

    internal ClipHandle(int typeSlot) => _slotPlusOne = checked(typeSlot + 1);

    internal int TypeSlot => _slotPlusOne - 1;

    public bool IsValid => _slotPlusOne != 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TimelineCursor
{
    public readonly TimelineId Timeline;
    public readonly int Tick;
    public readonly TimelineDirection Direction;

    public TimelineCursor(TimelineId timeline, int tick, TimelineDirection direction)
    {
        Timeline = timeline;
        Tick = tick;
        Direction = direction;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly struct TimelineSpan
{
    public readonly TimelineId Timeline;
    public readonly long PreviousRawTick;
    public readonly long CurrentRawTick;

    public TimelineSpan(TimelineId timeline, long previousRawTick, long currentRawTick)
    {
        Timeline = timeline;
        PreviousRawTick = previousRawTick;
        CurrentRawTick = currentRawTick;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TimelineHeader
{
    public readonly ulong Key;
    public readonly int Duration;
    public readonly TimelineFlags Flags;

    public TimelineHeader(ulong key, int duration, TimelineFlags flags)
    {
        Key = key;
        Duration = duration;
        Flags = flags;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TimelineLookupEntry
{
    public readonly ulong Key;
    public readonly int TimelineId;

    public TimelineLookupEntry(ulong key, int timelineId)
    {
        Key = key;
        TimelineId = timelineId;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TrackInstance
{
    public readonly int Binding;
    public readonly int TrackDataId;

    public TrackInstance(int binding, int trackDataId)
    {
        Binding = binding;
        TrackDataId = trackDataId;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TrackData
{
    public readonly ulong TypeKey;
    public readonly int ClipStart;
    public readonly int ClipCount;
    public readonly int LaneSplit;
    public readonly int BoundaryStart;
    public readonly int BoundaryCount;
    public readonly TrackMode Mode;

    public TrackData(
        ulong typeKey,
        int clipStart,
        int clipCount,
        int laneSplit,
        int boundaryStart,
        int boundaryCount,
        TrackMode mode)
    {
        TypeKey = typeKey;
        ClipStart = clipStart;
        ClipCount = clipCount;
        LaneSplit = laneSplit;
        BoundaryStart = boundaryStart;
        BoundaryCount = boundaryCount;
        Mode = mode;
    }
}

public readonly ref struct TrackRef
{
    public readonly ref readonly TrackInstance Instance;
    public readonly ref readonly TrackData Data;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TrackRef(in TrackInstance instance, in TrackData data)
    {
        Instance = ref instance;
        Data = ref data;
    }

    public int Binding => Instance.Binding;
    public int TrackDataId => Instance.TrackDataId;
    public TrackMode Mode => Data.Mode;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct ClipHeader
{
    public readonly int Start;
    public readonly int End;
    public readonly int DataOffset;
    public readonly ClipEase Ease;

    public ClipHeader(int start, int end, int dataOffset, ClipEase ease)
    {
        Start = start;
        End = end;
        DataOffset = dataOffset;
        Ease = ease;
    }

    public int Duration => End - Start;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct BoundaryHeader
{
    public readonly int Tick;
    public readonly int ClipA;
    public readonly int ClipB;
    public readonly BoundaryKind Kind;

    public BoundaryHeader(int tick, int clipA, int clipB, BoundaryKind kind)
    {
        Tick = tick;
        ClipA = clipA;
        ClipB = clipB;
        Kind = kind;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TypeDescriptor
{
    public readonly ulong Key;
    public readonly int Size;

    public TypeDescriptor(ulong key, int size)
    {
        Key = key;
        Size = size;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TypePartition
{
    public readonly int TrackStart;
    public readonly int TrackCount;

    public TypePartition(int trackStart, int trackCount)
    {
        TrackStart = trackStart;
        TrackCount = trackCount;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct ClipSample
{
    public readonly int DataOffset;
    public readonly float Weight;

    public ClipSample(int dataOffset, float weight)
    {
        DataOffset = dataOffset;
        Weight = weight;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct ClipHit
{
    public readonly int Start;
    public readonly int End;
    public readonly int Tick;
    public readonly int DataOffset;
    public readonly TimelineDirection Direction;
    public readonly BlendPhase BlendPhase;
    public readonly ClipEase Ease;
    public readonly float Weight;
    public readonly float BlendFactor;

    internal ClipHit(
        int start,
        int end,
        int tick,
        int dataOffset,
        TimelineDirection direction,
        BlendPhase blendPhase,
        ClipEase ease,
        float weight,
        float blendFactor)
    {
        Start = start;
        End = end;
        Tick = tick;
        DataOffset = dataOffset;
        Direction = direction;
        BlendPhase = blendPhase;
        Ease = ease;
        Weight = weight;
        BlendFactor = blendFactor;
    }

    public ClipPhase Phase =>
        TimelineMath.ClipPhaseAt(Tick, Start, End, Direction);

    public float Progress =>
        TimelineMath.Ease(Ease, TimelineMath.Progress(Tick, Start, End));
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct ClipTransition
{
    public readonly long OccurrenceTick;
    public readonly int Tick;
    public readonly TimelineDirection Direction;
    public readonly ClipPhase Phase;
    public readonly int DataOffset;

    internal ClipTransition(
        long occurrenceTick,
        int tick,
        TimelineDirection direction,
        ClipPhase phase,
        int dataOffset)
    {
        OccurrenceTick = occurrenceTick;
        Tick = tick;
        Direction = direction;
        Phase = phase;
        DataOffset = dataOffset;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct BlendTransition
{
    public readonly long OccurrenceTick;
    public readonly int Tick;
    public readonly TimelineDirection Direction;
    public readonly BlendPhase Phase;
    public readonly int DataOffsetA;
    public readonly int DataOffsetB;
    public readonly float Factor;

    internal BlendTransition(
        long occurrenceTick,
        int tick,
        TimelineDirection direction,
        BlendPhase phase,
        int dataOffsetA,
        int dataOffsetB,
        float factor)
    {
        OccurrenceTick = occurrenceTick;
        Tick = tick;
        Direction = direction;
        Phase = phase;
        DataOffsetA = dataOffsetA;
        DataOffsetB = dataOffsetB;
        Factor = factor;
    }
}
