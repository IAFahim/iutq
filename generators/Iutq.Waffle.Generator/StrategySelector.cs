namespace Iutq.Waffle.Generator;

/// <summary>
///     Chooses the per-track lookup strategy. Mirrors the external proposal's
///     adaptive selector, with one deviation: exclusive LUTs are payload-DIRECT
///     (tick to offset, no clip indirection) and crossfades bake the final
///     per-tick weight instead of a reciprocal.
/// </summary>
internal static class StrategySelector
{
    public const int MaxDense8Duration = 512;
    public const int MaxLutDuration = 2048;
    public const int MaxTinyClips = 4;
    public const int Dense8MaxOffset = 254; // leave 255 as the sentinel

    public static LookupStrategyKind Select(TrackInfo track, int duration)
    {
        if (track.CrossFade)
        {
            if (duration > MaxLutDuration)
                throw new InvalidDataException(
                    $"CrossFade track with duration {duration} exceeds the {MaxLutDuration}-tick " +
                    "blend-LUT cap of this experiment.");

            return LookupStrategyKind.BlendLut;
        }

        var clipCount = track.ClipCount;

        if (clipCount <= MaxTinyClips)
            return LookupStrategyKind.Tiny;

        var maxOffset = 0;

        foreach (var clip in track.LaneA)
            maxOffset = Math.Max(maxOffset, clip.Offset);

        foreach (var clip in track.LaneB)
            maxOffset = Math.Max(maxOffset, clip.Offset);

        if (duration <= MaxDense8Duration && maxOffset <= Dense8MaxOffset)
            return LookupStrategyKind.Dense8;

        if (duration <= MaxLutDuration)
            return LookupStrategyKind.Dense16;

        return LookupStrategyKind.Binary;
    }
}
