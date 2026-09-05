using System.Runtime.CompilerServices;

namespace Iutq.Core.Primitives;

public static class TimelineMath
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Ease(ClipEase ease, float t)
    {
        return ease switch
        {
            ClipEase.Linear => t,
            ClipEase.QuadIn => t * t,
            ClipEase.QuadOut => t * (2f - t),
            ClipEase.CubicInOut => t < 0.5f
                ? 4f * t * t * t
                : 1f - (-2f * t + 2f) * (-2f * t + 2f) * (-2f * t + 2f) * 0.5f,
            _ => t
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Progress(int tick, int start, int end)
    {
        var duration = end - start;

        if (duration <= 1) return 0f;

        return (float)(tick - start) / (duration - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ClipPhase ClipPhaseAt(
        int tick,
        int start,
        int end,
        TimelineDirection direction)
    {
        var duration = end - start;

        if (duration == 1)
            return direction == TimelineDirection.None
                ? ClipPhase.None
                : ClipPhase.Enter;

        if (direction == TimelineDirection.Forward)
        {
            if (tick == start) return ClipPhase.Enter;

            if (tick == end - 1) return ClipPhase.Exit;
        }
        else if (direction == TimelineDirection.Reverse)
        {
            if (tick == end - 1) return ClipPhase.Enter;

            if (tick == start) return ClipPhase.Exit;
        }

        return ClipPhase.Active;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BlendPhase BlendPhaseAt(
        int tick,
        int start,
        int end,
        TimelineDirection direction)
    {
        var duration = end - start;

        if (duration == 1)
            return direction == TimelineDirection.None
                ? BlendPhase.None
                : BlendPhase.Enter;

        if (direction == TimelineDirection.Forward)
        {
            if (tick == start) return BlendPhase.Enter;

            if (tick == end - 1) return BlendPhase.Exit;
        }
        else if (direction == TimelineDirection.Reverse)
        {
            if (tick == end - 1) return BlendPhase.Enter;

            if (tick == start) return BlendPhase.Exit;
        }

        return BlendPhase.Active;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float BlendFactor(int tick, int start, int end)
    {
        var duration = end - start;

        if (duration <= 1) return 0.5f;

        return (float)(tick - start) / (duration - 1);
    }
}