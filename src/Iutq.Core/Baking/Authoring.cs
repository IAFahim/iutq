using Iutq.Core.Primitives;

namespace Iutq.Core.Baking;

public readonly record struct ClipDefinition<TClip>(int Start, int End, TClip Data, ClipEase Ease = ClipEase.Linear)
    where TClip : unmanaged;

public static class Clip
{
    public static ClipDefinition<TClip> Range<TClip>(int start, int end, in TClip data, ClipEase ease = ClipEase.Linear)
        where TClip : unmanaged => new(start, end, data, ease);

    public static ClipDefinition<TClip> At<TClip>(int tick, in TClip data) 
        where TClip : unmanaged => new(tick, checked(tick + 1), data);
}