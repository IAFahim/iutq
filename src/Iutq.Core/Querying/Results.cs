using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Iutq.Core.Primitives;
using Iutq.Core.Storage;

namespace Iutq.Core.Querying;

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

    /// <summary>Public so source-generated frozen kernels can construct transitions directly.</summary>
    public ClipTransition(
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

    /// <summary>Public so source-generated frozen kernels can construct transitions directly.</summary>
    public BlendTransition(
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
