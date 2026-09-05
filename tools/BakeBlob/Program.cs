using System.Runtime.InteropServices;
using Iutq.Core.Baking;
using Iutq.Core.Primitives;
using Iutq.Core.Storage;

namespace BakeBlob;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BenchClip
{
    public float A;
    public float B;
}

public static class BenchTypes
{
    public static readonly ClipType<BenchClip> Clip = new(0x5100000000000001UL);
}

/// <summary>
///     Payload used by the generator-parity fixture. Shares the type key and 4-byte
///     layout of <c>Iutq.Tests.TestClip</c> so the emitted blob is resolvable there.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FixtureClip
{
    public int Value;
}

public static class FixtureTypes
{
    public static readonly ClipType<FixtureClip> Clip = new(0x7000000000000001UL);
}

/// <summary>
///     Bakes the committed .iutq blobs consumed as AdditionalFiles by the Waffle
///     generator. The three bench blobs replicate the builder sequence in
///     bench/Iutq.Bench/TimelineBenchmarks.cs GlobalSetup byte for byte; the
///     fixture blob exercises every lookup strategy in the generator's selector.
/// </summary>
public static class Program
{
    public static int Main()
    {
        var root = FindRepoRoot();
        Write(Path.Combine(root, "bench", "Iutq.Bench", "Timelines.iutq"), BuildBenchExclusive());
        Write(Path.Combine(root, "bench", "Iutq.Bench", "CrossFade.iutq"), BuildBenchCrossFade());
        Write(Path.Combine(root, "bench", "Iutq.Bench", "Pulse.iutq"), BuildBenchPulse());
        Write(Path.Combine(root, "tests", "Iutq.Tests", "Strategies.iutq"), BuildStrategyFixture());
        return 0;
    }

    /// <summary>Replicates the exclusive part of TimelineBenchmarks.Setup exactly.</summary>
    public static TimelineDatabase BuildBenchExclusive()
    {
        DatabaseBuilder exclusiveBuilder = new();

        for (var i = 0; i < 8; i++)
        {
            var timeline = exclusiveBuilder.AddTimeline(new TimelineKey(0xA100UL + (ulong)i), 64);
            var clips = new ClipDefinition<BenchClip>[4];

            for (var c = 0; c < 4; c++)
                clips[c] = Clip.Range(c * 16, c * 16 + 16, new BenchClip { A = 10f * i + c, B = 0.5f * c });

            exclusiveBuilder.AddTrack(timeline, BenchTypes.Clip, new BindingId(i), TrackMode.Exclusive, clips);
        }

        return exclusiveBuilder.Build();
    }

    /// <summary>Replicates the crossfade part of TimelineBenchmarks.Setup exactly.</summary>
    public static TimelineDatabase BuildBenchCrossFade()
    {
        DatabaseBuilder crossFadeBuilder = new();
        var crossFadeTimeline = crossFadeBuilder.AddTimeline(new TimelineKey(0xB1), 64);
        ClipDefinition<BenchClip>[] crossFadeClips =
        [
            Clip.Range(0, 24, new BenchClip { A = 1f, B = 0.1f }),
            Clip.Range(16, 48, new BenchClip { A = 2f, B = 0.2f }),
            Clip.Range(40, 64, new BenchClip { A = 3f, B = 0.3f })
        ];
        crossFadeBuilder.AddTrack(crossFadeTimeline, BenchTypes.Clip, new BindingId(0), TrackMode.CrossFade,
            crossFadeClips);
        return crossFadeBuilder.Build();
    }

    /// <summary>Replicates the pulse part of TimelineBenchmarks.Setup exactly.</summary>
    public static TimelineDatabase BuildBenchPulse()
    {
        DatabaseBuilder pulseBuilder = new();
        var pulseTimeline = pulseBuilder.AddTimeline(new TimelineKey(0xC1), 64, TimelineFlags.Loop);
        ClipDefinition<BenchClip>[] pulseClips =
        [
            Clip.At(5, new BenchClip { A = 5f, B = 0.5f }),
            Clip.Range(8, 20, new BenchClip { A = 8f, B = 0.8f }),
            Clip.At(21, new BenchClip { A = 21f, B = 2.1f }),
            Clip.At(40, new BenchClip { A = 40f, B = 4f })
        ];
        pulseBuilder.AddTrack(pulseTimeline, BenchTypes.Clip, new BindingId(0), TrackMode.Exclusive, pulseClips);
        return pulseBuilder.Build();
    }

    /// <summary>
    ///     Generator-selector coverage: one timeline per strategy, plus loop and
    ///     reverse traversal fixtures. Strategies: Tiny (D5), Dense8 (D0),
    ///     Dense16 (D1), Binary (D2), BlendLut (D3, D6), Binary crossfade (D4),
    ///     Binary loop (D7), Tiny loop (D5).
    /// </summary>
    public static TimelineDatabase BuildStrategyFixture()
    {
        DatabaseBuilder builder = new();

        var dense8 = builder.AddTimeline(new TimelineKey(0xD0), 512);
        var dense16 = builder.AddTimeline(new TimelineKey(0xD1), 600);
        var binary = builder.AddTimeline(new TimelineKey(0xD2), 2100);
        var blendLut = builder.AddTimeline(new TimelineKey(0xD3), 600);
        var blendBinary = builder.AddTimeline(new TimelineKey(0xD4), 2100);
        var tinyLoop = builder.AddTimeline(new TimelineKey(0xD5), 10, TimelineFlags.Loop);
        var blendLutLoop = builder.AddTimeline(new TimelineKey(0xD6), 700, TimelineFlags.Loop);
        var binaryLoop = builder.AddTimeline(new TimelineKey(0xD7), 2200, TimelineFlags.Loop);

        // Dense8: 20 clips of 25 ticks, then 12 idle ticks; offsets stay <= 254.
        var dense8Clips = new ClipDefinition<FixtureClip>[20];

        for (var c = 0; c < 20; c++)
            dense8Clips[c] = Clip.Range(c * 25, c * 25 + 25, new FixtureClip { Value = 100 + c });

        builder.AddTrack(dense8, FixtureTypes.Clip, new BindingId(0), TrackMode.Exclusive, dense8Clips);

        // Dense16: 100 clips of 6 ticks.
        var dense16Clips = new ClipDefinition<FixtureClip>[100];

        for (var c = 0; c < 100; c++)
            dense16Clips[c] = Clip.Range(c * 6, c * 6 + 6, new FixtureClip { Value = c });

        builder.AddTrack(dense16, FixtureTypes.Clip, new BindingId(0), TrackMode.Exclusive, dense16Clips);

        // Binary: 9 ranges of 190 ticks plus a pulse.
        var binaryClips = new ClipDefinition<FixtureClip>[10];

        for (var c = 0; c < 9; c++)
            binaryClips[c] = Clip.Range(c * 210 + 10, c * 210 + 200, new FixtureClip { Value = 200 + c });

        binaryClips[9] = Clip.At(2095, new FixtureClip { Value = 999 });
        builder.AddTrack(binary, FixtureTypes.Clip, new BindingId(0), TrackMode.Exclusive, binaryClips);

        // BlendLut: two lanes, three overlapping pairs.
        builder.AddTrack(
            blendLut,
            FixtureTypes.Clip,
            new BindingId(0),
            TrackMode.CrossFade,
            [
                Clip.Range(0, 300, new FixtureClip { Value = 1 }),
                Clip.Range(150, 450, new FixtureClip { Value = 2 }),
                Clip.Range(300, 600, new FixtureClip { Value = 3 }),
                Clip.Range(450, 600, new FixtureClip { Value = 4 })
            ]);

        // Binary crossfade: single pair, long overlap.
        builder.AddTrack(
            blendBinary,
            FixtureTypes.Clip,
            new BindingId(0),
            TrackMode.CrossFade,
            [
                Clip.Range(0, 1200, new FixtureClip { Value = 11 }),
                Clip.Range(600, 2100, new FixtureClip { Value = 22 })
            ]);

        // Tiny loop: pulse + short range.
        builder.AddTrack(
            tinyLoop,
            FixtureTypes.Clip,
            new BindingId(0),
            TrackMode.Exclusive,
            [
                Clip.At(0, new FixtureClip { Value = 31 }),
                Clip.Range(2, 6, new FixtureClip { Value = 32 })
            ]);

        // BlendLut loop.
        builder.AddTrack(
            blendLutLoop,
            FixtureTypes.Clip,
            new BindingId(0),
            TrackMode.CrossFade,
            [
                Clip.Range(0, 400, new FixtureClip { Value = 41 }),
                Clip.Range(200, 600, new FixtureClip { Value = 42 }),
                Clip.Range(500, 700, new FixtureClip { Value = 43 })
            ]);

        // Binary loop: 8 clips of 275 ticks.
        var binaryLoopClips = new ClipDefinition<FixtureClip>[8];

        for (var c = 0; c < 8; c++)
            binaryLoopClips[c] = Clip.Range(c * 275, c * 275 + 275, new FixtureClip { Value = 50 + c });

        builder.AddTrack(binaryLoop, FixtureTypes.Clip, new BindingId(0), TrackMode.Exclusive, binaryLoopClips);

        return builder.Build();
    }

    private static void Write(string path, TimelineDatabase database)
    {
        var blob = database.ToArray();
        File.WriteAllBytes(path, blob);
        Console.WriteLine($"{path}: {blob.Length} bytes");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Iutq.slnx")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException("Could not locate the repository root (Iutq.slnx not found).");

        return dir.FullName;
    }
}
