using System.Runtime.InteropServices;
using Iutq.Core;

namespace Iutq.Demo;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ForceClip
{
    public float Forward;
    public float Up;

    public ForceClip(float forward, float up)
    {
        Forward = forward;
        Up = up;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct DamagePulseClip
{
    public int Damage;

    public DamagePulseClip(int damage)
    {
        Damage = damage;
    }
}

public static class GameClipTypes
{
    public static readonly ClipType<ForceClip> Force = new(0x91A22101F0010001UL);
    public static readonly ClipType<DamagePulseClip> DamagePulse = new(0x91A22101D0010001UL);
}

public static class GameTimelines
{
    public static readonly TimelineKey LightAttack = new(0xA771000000000001UL);
    public static readonly TimelineKey Dash = new(0xA771000000000002UL);
}

public struct ForceAccumulator : IClipSampleVisitor<ForceClip>
{
    public float Forward;
    public float Up;

    public void Sample(in TrackInstance track, in ForceClip clip, float weight)
    {
        Forward += clip.Forward * weight;
        Up += clip.Up * weight;
    }
}

public struct TotalForceVisitor : IClipFrameVisitor<ForceClip>
{
    public float Forward;
    public float Up;

    public void Visit(in TrackInstance track, in ClipFrame frame, in ForceClip clip)
    {
        Forward += clip.Forward * frame.Weight;
        Up += clip.Up * frame.Weight;
    }
}

public struct DamageTransitions : IClipTransitionVisitor<DamagePulseClip>
{
    public int Damage;

    public void OnClipTransition(in TrackInstance track, in ClipTransition transition, in DamagePulseClip clip)
    {
        if (transition.Phase == ClipPhase.Enter) Damage += clip.Damage;
    }

    public void OnBlendTransition(
        in TrackInstance track,
        in BlendTransition transition,
        in DamagePulseClip clipA,
        in DamagePulseClip clipB)
    {
    }
}

public sealed class TimelineRuntime
{
    public readonly ClipTypeHandle<DamagePulseClip> DamagePulse;
    public readonly TimelineIndex Dash;
    public readonly TimelineDatabase Database;
    public readonly ClipTypeHandle<ForceClip> Force;
    public readonly TimelineIndex LightAttack;

    private TimelineRuntime(
        TimelineDatabase database,
        TimelineIndex lightAttack,
        TimelineIndex dash,
        ClipTypeHandle<ForceClip> force,
        ClipTypeHandle<DamagePulseClip> damagePulse)
    {
        Database = database;
        LightAttack = lightAttack;
        Dash = dash;
        Force = force;
        DamagePulse = damagePulse;
    }

    public static TimelineRuntime Build()
    {
        DatabaseBuilder builder = new();
        var lightAttack = builder.AddTimeline(GameTimelines.LightAttack, 30);
        var dash = builder.AddTimeline(GameTimelines.Dash, 12);

        ForceClip attackForce = new(14f, 2f);
        ForceClip attackRecovery = new(4f, 0f);
        ForceClip dashForce = new(24f, 0f);
        DamagePulseClip pulse = new(35);

        builder.AddTrack(
            lightAttack,
            GameClipTypes.Force,
            new BindingId(0),
            TrackMode.CrossFade,
            [
                Clip.Range(4, 10, in attackForce),
                Clip.Range(8, 14, in attackRecovery)
            ]);

        builder.AddTrack(
            lightAttack,
            GameClipTypes.DamagePulse,
            new BindingId(0),
            TrackMode.Exclusive,
            [
                Clip.At(7, in pulse)
            ]);

        builder.AddTrack(
            dash,
            GameClipTypes.Force,
            new BindingId(0),
            TrackMode.Exclusive,
            [
                Clip.Range(0, 6, in dashForce)
            ]);

        var database = builder.Build();

        return new TimelineRuntime(
            database,
            lightAttack,
            dash,
            database.Resolve(GameClipTypes.Force),
            database.Resolve(GameClipTypes.DamagePulse));
    }
}

public static class ForceSystem
{
    public static void Execute(
        TimelineRuntime runtime,
        ReadOnlySpan<TimelineCursor> activeTimelines,
        out float forward,
        out float up)
    {
        var db = runtime.Database.AsView();
        var force = db.Query(runtime.Force);
        int[] bindingEntity = [0];
        Span<ForceAccumulator> accumulators = stackalloc ForceAccumulator[1];

        foreach (ref readonly var cursor in activeTimelines)
        foreach (ref readonly var track in force.Tracks(cursor.Timeline))
        {
            ref var accumulator = ref accumulators[bindingEntity[track.Binding]];
            force.Sample(in track, cursor.Tick, ref accumulator);
        }

        forward = accumulators[0].Forward;
        up = accumulators[0].Up;
    }
}

public static class DamageSystem
{
    public static int CatchUp(
        TimelineRuntime runtime,
        in TimelineSpan movement)
    {
        var db = runtime.Database.AsView();
        var damage = db.Query(runtime.DamagePulse);
        DamageTransitions transitions = default;

        damage.TraverseTransitions(in movement, ref transitions);
        return transitions.Damage;
    }
}

public static class Program
{
    public static void Main()
    {
        var runtime = TimelineRuntime.Build();

        Span<TimelineCursor> playerTimelines = stackalloc TimelineCursor[2];
        playerTimelines[0] = new TimelineCursor(runtime.LightAttack, 8, TimelineDirection.Forward);
        playerTimelines[1] = new TimelineCursor(runtime.Dash, 3, TimelineDirection.Forward);

        ForceSystem.Execute(runtime, playerTimelines, out var entityForward, out var entityUp);

        var db = runtime.Database.AsView();
        var force = db.Query(runtime.Force);
        TotalForceVisitor visitor = default;
        force.VisitFrames(playerTimelines, ref visitor);

        var damage = DamageSystem.CatchUp(
            runtime,
            new TimelineSpan(runtime.LightAttack, 6, 7));

        Console.WriteLine($"Force (entity loop): {entityForward}, {entityUp}");
        Console.WriteLine($"Force (visit): {visitor.Forward}, {visitor.Up}");
        Console.WriteLine($"Damage: {damage}");
    }
}