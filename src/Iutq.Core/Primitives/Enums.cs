namespace Iutq.Core.Primitives;

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
