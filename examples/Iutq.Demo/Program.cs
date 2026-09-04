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

    public DamagePulseClip(int damage) => Damage = damage;
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

public struct ForceAccumulator : IClipFrameVisitor<ForceClip>
{
    public float Forward;
    public float Up;

    public void Visit(in ClipHit hit, in ForceClip clip)
    {
        Forward += clip.Forward * hit.Weight;
        Up += clip.Up * hit.Weight;
    }
}

public struct DamageTransitions : IClipTransitionVisitor<DamagePulseClip>
{
    public int Damage;

    public void Clip(in ClipTransition transition, in DamagePulseClip clip)
    {
        if (transition.Phase == ClipPhase.Enter)
        {
            Damage += clip.Damage;
        }
    }

    public void Blend(
        in BlendTransition transition,
        in DamagePulseClip clipA,
        in DamagePulseClip clipB)
    {
    }
}

public sealed class TimelineRuntime
{
    public readonly TimelineDatabase Database;
    public readonly TimelineId LightAttack;
    public readonly TimelineId Dash;
    public readonly ClipHandle<ForceClip> Force;
    public readonly ClipHandle<DamagePulseClip> DamagePulse;

    private TimelineRuntime(
        TimelineDatabase database,
        TimelineId lightAttack,
        TimelineId dash,
        ClipHandle<ForceClip> force,
        ClipHandle<DamagePulseClip> damagePulse)
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
        TimelineId lightAttack = builder.AddTimeline(GameTimelines.LightAttack, 30);
        TimelineId dash = builder.AddTimeline(GameTimelines.Dash, 12);

        ForceClip attackForce = new(14f, 2f);
        ForceClip attackRecovery = new(4f, 0f);
        ForceClip dashForce = new(24f, 0f);
        DamagePulseClip hit = new(35);

        builder.AddTrack(
            lightAttack,
            GameClipTypes.Force,
            new BindingId(0),
            TrackMode.CrossFade,
            [
                Clip.Range(4, 10, in attackForce),
                Clip.Range(8, 14, in attackRecovery),
            ]);

        builder.AddTrack(
            lightAttack,
            GameClipTypes.DamagePulse,
            new BindingId(0),
            TrackMode.Exclusive,
            [
                Clip.At(7, in hit),
            ]);

        builder.AddTrack(
            dash,
            GameClipTypes.Force,
            new BindingId(0),
            TrackMode.Exclusive,
            [
                Clip.Range(0, 6, in dashForce),
            ]);

        TimelineDatabase database = builder.Build();

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
        DatabaseView db = runtime.Database.AsView();
        ClipQuery<ForceClip> force = db.Query(runtime.Force);
        ForceAccumulator accumulator = default;

        force.Visit(activeTimelines, ref accumulator);

        forward = accumulator.Forward;
        up = accumulator.Up;
    }
}

public static class DamageSystem
{
    public static int CatchUp(
        TimelineRuntime runtime,
        in TimelineSpan movement)
    {
        DatabaseView db = runtime.Database.AsView();
        ClipQuery<DamagePulseClip> damage = db.Query(runtime.DamagePulse);
        DamageTransitions transitions = default;

        damage.TraverseTransitions(in movement, ref transitions);
        return transitions.Damage;
    }
}

public static class Program
{
    public static void Main()
    {
        TimelineRuntime runtime = TimelineRuntime.Build();

        Span<TimelineCursor> playerTimelines = stackalloc TimelineCursor[2];
        playerTimelines[0] = new TimelineCursor(runtime.LightAttack, 8, TimelineDirection.Forward);
        playerTimelines[1] = new TimelineCursor(runtime.Dash, 3, TimelineDirection.Forward);

        ForceSystem.Execute(runtime, playerTimelines, out float forward, out float up);
        int damage = DamageSystem.CatchUp(
            runtime,
            new TimelineSpan(runtime.LightAttack, 6, 7));

        Console.WriteLine($"Force: {forward}, {up}");
        Console.WriteLine($"Damage: {damage}");
    }
}
