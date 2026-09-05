using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Iutq.Core;

#pragma warning disable CA1711
[Flags]
public enum TimelineFlags : byte
{
    None = 0,
    Loop = 1
}
#pragma warning restore CA1711

public enum TrackMode : byte
{
    Exclusive = 0,
    CrossFade = 1
}

public enum ClipEase : byte
{
    Linear = 0,
    QuadIn = 1,
    QuadOut = 2,
    CubicInOut = 3
}

public enum TimelineDirection : sbyte
{
    Reverse = -1,
    None = 0,
    Forward = 1
}

public enum ClipPhase : byte
{
    None = 0,
    Enter = 1,
    Active = 2,
    Exit = 3
}

public enum BlendPhase : byte
{
    None = 0,
    Enter = 1,
    Active = 2,
    Exit = 3
}

internal enum BoundaryKind : byte
{
    ClipStart = 0,
    BlendStart = 1,
    ClipInstant = 2,
    BlendInstant = 3,
    BlendEnd = 4,
    ClipEnd = 5
}

public readonly record struct TimelineKey(ulong Value);

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TimelineIndex
{
    public readonly int Value;

    public TimelineIndex(int value)
    {
        Value = value;
    }

    public static readonly TimelineIndex None = new(-1);

    public bool IsValid => Value >= 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct BindingId
{
    public readonly int Value;

    public BindingId(int value)
    {
        Value = value;
    }

    public static readonly BindingId None = new(-1);
}

[SuppressMessage("ReSharper", "UnusedTypeParameter",
    Justification =
        "Phantom type parameter: statically binds a clip type to its payload type with no runtime representation.")]
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly struct ClipType<T>
    where T : unmanaged
{
    public readonly ulong Key;

    public ClipType(ulong key)
    {
        Check.TypeKeyNonZero(key);

        Key = key;
    }
}

[SuppressMessage("ReSharper", "UnusedTypeParameter",
    Justification =
        "Phantom type parameter: statically binds a handle to its payload type with no runtime representation.")]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct ClipTypeHandle<T>
    where T : unmanaged
{
    private readonly int _slotPlusOne;

    internal ClipTypeHandle(int typeSlot)
    {
        _slotPlusOne = Check.SlotPlusOne(typeSlot);
    }

    internal int TypeSlot => _slotPlusOne - 1;

    public bool IsValid => _slotPlusOne != 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TimelineCursor
{
    public readonly TimelineIndex Timeline;
    public readonly int Tick;
    public readonly TimelineDirection Direction;

    public TimelineCursor(TimelineIndex timeline, int tick, TimelineDirection direction)
    {
        Timeline = timeline;
        Tick = tick;
        Direction = direction;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly struct TimelineSpan
{
    public readonly TimelineIndex Timeline;
    public readonly long PreviousRawTick;
    public readonly long CurrentRawTick;

    public TimelineSpan(TimelineIndex timeline, long previousRawTick, long currentRawTick)
    {
        Timeline = timeline;
        PreviousRawTick = previousRawTick;
        CurrentRawTick = currentRawTick;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TimelineHeader : IEquatable<TimelineHeader>
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

    public bool Equals(TimelineHeader other)
    {
        return Key == other.Key && Duration == other.Duration && Flags == other.Flags;
    }

    public override bool Equals(object? obj)
    {
        return obj is TimelineHeader other && Equals(other);
    }

    public static bool operator ==(TimelineHeader left, TimelineHeader right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(TimelineHeader left, TimelineHeader right)
    {
        return !left.Equals(right);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Key, Duration, Flags);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TimelineLookupEntry : IEquatable<TimelineLookupEntry>
{
    public readonly ulong Key;
    public readonly int TimelineIndex;

    public TimelineLookupEntry(ulong key, int timelineIndex)
    {
        Key = key;
        TimelineIndex = timelineIndex;
    }

    public bool Equals(TimelineLookupEntry other)
    {
        return Key == other.Key && TimelineIndex == other.TimelineIndex;
    }

    public override bool Equals(object? obj)
    {
        return obj is TimelineLookupEntry other && Equals(other);
    }

    public static bool operator ==(TimelineLookupEntry left, TimelineLookupEntry right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(TimelineLookupEntry left, TimelineLookupEntry right)
    {
        return !left.Equals(right);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Key, TimelineIndex);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TrackInstance : IEquatable<TrackInstance>
{
    public readonly int Binding;
    public readonly int TrackTemplateId;

    public TrackInstance(int binding, int trackDataId)
    {
        Binding = binding;
        TrackTemplateId = trackDataId;
    }

    public bool Equals(TrackInstance other)
    {
        return Binding == other.Binding && TrackTemplateId == other.TrackTemplateId;
    }

    public override bool Equals(object? obj)
    {
        return obj is TrackInstance other && Equals(other);
    }

    public static bool operator ==(TrackInstance left, TrackInstance right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(TrackInstance left, TrackInstance right)
    {
        return !left.Equals(right);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Binding, TrackTemplateId);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TrackTemplate : IEquatable<TrackTemplate>
{
    public readonly ulong TypeKey;
    public readonly int ClipStart;
    public readonly int ClipCount;
    public readonly int LaneSplit;
    public readonly int BoundaryStart;
    public readonly int BoundaryCount;
    public readonly TrackMode Mode;

    public TrackTemplate(
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

    public bool Equals(TrackTemplate other)
    {
        return TypeKey == other.TypeKey &&
               ClipStart == other.ClipStart &&
               ClipCount == other.ClipCount &&
               LaneSplit == other.LaneSplit &&
               BoundaryStart == other.BoundaryStart &&
               BoundaryCount == other.BoundaryCount &&
               Mode == other.Mode;
    }

    public override bool Equals(object? obj)
    {
        return obj is TrackTemplate other && Equals(other);
    }

    public static bool operator ==(TrackTemplate left, TrackTemplate right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(TrackTemplate left, TrackTemplate right)
    {
        return !left.Equals(right);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(TypeKey, ClipStart, ClipCount, LaneSplit, BoundaryStart, BoundaryCount, Mode);
    }
}

public readonly ref struct TrackRef
{
    public readonly ref readonly TrackInstance Instance;
    public readonly ref readonly TrackTemplate Template;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TrackRef(in TrackInstance instance, in TrackTemplate template)
    {
        Instance = ref instance;
        Template = ref template;
    }

    public int Binding => Instance.Binding;
    public int TrackTemplateId => Instance.TrackTemplateId;
    public TrackMode Mode => Template.Mode;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct ClipEntry : IEquatable<ClipEntry>
{
    public readonly int Start;
    public readonly int End;
    public readonly int DataOffset;
    public readonly ClipEase Ease;

    public ClipEntry(int start, int end, int dataOffset, ClipEase ease)
    {
        Start = start;
        End = end;
        DataOffset = dataOffset;
        Ease = ease;
    }

    public bool Equals(ClipEntry other)
    {
        return Start == other.Start && End == other.End && DataOffset == other.DataOffset && Ease == other.Ease;
    }

    public override bool Equals(object? obj)
    {
        return obj is ClipEntry other && Equals(other);
    }

    public static bool operator ==(ClipEntry left, ClipEntry right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(ClipEntry left, ClipEntry right)
    {
        return !left.Equals(right);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Start, End, DataOffset, Ease);
    }

    public int Duration => End - Start;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct TrackBoundary : IEquatable<TrackBoundary>
{
    public readonly int Tick;
    public readonly int ClipA;
    public readonly int ClipB;
    public readonly BoundaryKind Kind;

    public TrackBoundary(int tick, int clipA, int clipB, BoundaryKind kind)
    {
        Tick = tick;
        ClipA = clipA;
        ClipB = clipB;
        Kind = kind;
    }

    public bool Equals(TrackBoundary other)
    {
        return Tick == other.Tick && ClipA == other.ClipA && ClipB == other.ClipB && Kind == other.Kind;
    }

    public override bool Equals(object? obj)
    {
        return obj is TrackBoundary other && Equals(other);
    }

    public static bool operator ==(TrackBoundary left, TrackBoundary right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(TrackBoundary left, TrackBoundary right)
    {
        return !left.Equals(right);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Tick, ClipA, ClipB, Kind);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TypeDescriptor : IEquatable<TypeDescriptor>
{
    public readonly ulong Key;
    public readonly int Size;

    public TypeDescriptor(ulong key, int size)
    {
        Key = key;
        Size = size;
    }

    public bool Equals(TypeDescriptor other)
    {
        return Key == other.Key && Size == other.Size;
    }

    public override bool Equals(object? obj)
    {
        return obj is TypeDescriptor other && Equals(other);
    }

    public static bool operator ==(TypeDescriptor left, TypeDescriptor right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(TypeDescriptor left, TypeDescriptor right)
    {
        return !left.Equals(right);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Key, Size);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct TrackSpan : IEquatable<TrackSpan>
{
    public readonly int TrackStart;
    public readonly int TrackCount;

    public TrackSpan(int trackStart, int trackCount)
    {
        TrackStart = trackStart;
        TrackCount = trackCount;
    }

    public bool Equals(TrackSpan other)
    {
        return TrackStart == other.TrackStart && TrackCount == other.TrackCount;
    }

    public override bool Equals(object? obj)
    {
        return obj is TrackSpan other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(TrackStart, TrackCount);
    }

    public static bool operator ==(TrackSpan left, TrackSpan right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(TrackSpan left, TrackSpan right)
    {
        return !left.Equals(right);
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
public readonly struct ClipFrame
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

    internal ClipFrame(
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
    public readonly long GlobalTick;
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
        GlobalTick = occurrenceTick;
        Tick = tick;
        Direction = direction;
        Phase = phase;
        DataOffset = dataOffset;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct BlendTransition
{
    public readonly long GlobalTick;
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
        GlobalTick = occurrenceTick;
        Tick = tick;
        Direction = direction;
        Phase = phase;
        DataOffsetA = dataOffsetA;
        DataOffsetB = dataOffsetB;
        Factor = factor;
    }
}