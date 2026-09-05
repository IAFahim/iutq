# iutq

**I**mmutable **U**nique **T**imeline **Q**uery — V3 of the timeline engine lineage (`TimelineEngine` V1/V2 → `iutq`).

Pure .NET 10. One immutable blob, stateless queries, zero allocations at steady state. No source generators, no wrapper cascades, no runtime playback ownership.

## Runtime model

The engine does not own playback instances. Gameplay/ECS owns the active timeline cursors:

```cs
public readonly struct TimelineCursor
{
    public readonly TimelineIndex Timeline;
    public readonly int Tick;
    public readonly TimelineDirection Direction;
}
```

Any number of timelines may affect an entity. The core API accepts a caller-owned `ReadOnlySpan<TimelineCursor>` and allocates nothing while querying it.

```text
ECS state
  TimelineCursor[]
       |
       v
ClipQuery<ForceClip>
       |
       v
all ForceClip tracks from all supplied timelines
       |
       v
one caller-owned accumulator
```

There is no runtime `TimelineInstance`, no core timeline pool, no lock, no per-frame clip-state array and no global maximum active-timeline count.

## One clip domain type

```cs
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ForceClip
{
    public float Forward;
    public float Up;
}
```

There is no `ForceValue` and no mandatory interpolator type. A clip payload is stored data; runtime behavior is supplied by the system consuming the query. (The baker enforces `LayoutKind.Sequential, Pack = 1` on every payload type.)

## Track model

```text
Timeline/type directory
        |
        v
TrackInstance          per-placement state only
  Binding
  TrackTemplateId
        |
        v
TrackTemplate              interned shared content
  TypeKey
  Clip window
  Lane split
  Mode
  Boundary window
        |
        +----> ClipEntry[]
        |
        +----> TrackBoundary[]
        |
        +----> interned payload arena
```

Structural deduplication: 10,000 enemies playing the same attack share one interned clip window, one `TrackTemplate` and one set of payloads. Each pays only a 8-byte `TrackInstance` row.

## Lifecycle semantics

Clips use `[Start, End)` storage. For `[10, 15)` the active ticks are `10..14`.

```text
Forward:  10 Enter, 11 Active, 12 Active, 13 Active, 14 Exit
Reverse:  14 Enter, 13 Active, 12 Active, 11 Active, 10 Exit
```

A one-frame `[10, 11)` clip is a pulse: `Enter` in both directions, never `Exit`. Paused multi-frame clips report `Active`; paused one-frame pulses report `None` so the pulse is not retriggered every frame.

## Crossfade semantics

`TrackMode.CrossFade` is explicitly pairwise — the baker rejects a third simultaneous clip. For two active clips each `ClipFrame` carries a normalized `Weight` and the current `BlendPhase`. The same baked overlap runs backward as the tick decreases; no separate reverse data set exists.

## Type-major database layout

```text
Directory[typeSlot * timelineCount + timelineIndex] -> TrackSpan { TrackStart, TrackCount }
```

A system resolves its clip type once:

```cs
ClipTypeHandle<ForceClip> forceHandle = database.Resolve(GameClipTypes.Force);
```

then every frame:

```cs
DatabaseView db = database.AsView();
ClipQuery<ForceClip> force = db.Query(forceHandle);
```

All lookups after binding are dense integer indexing. No process-wide generic type cache (the V2 `TypeSlotCache` and its volatile traffic are gone).

## Baking

```cs
DatabaseBuilder builder = new();
TimelineIndex attack = builder.AddTimeline(new TimelineKey(1), 30);

builder.AddTrack(
    attack,
    GameClipTypes.Force,
    new BindingId(0),
    TrackMode.CrossFade,
    [
        Clip.Range(4, 10, in attackForce),
        Clip.Range(8, 14, in attackRecovery),
    ]);

TimelineDatabase database = builder.Build();
```

Bake-time work: payload interning, clip-window interning, `TrackTemplate` interning, deterministic type/timeline partitioning, pairwise crossfade validation, lifecycle/blend boundary baking, blob assembly, structural validation and payload hashing. `Build()` runs the same full validator as `Load()`; after construction the database is proof that the invariants hold and the query kernel trusts it.

`TimelineDatabase.Load(ReadOnlySpan<byte>)` re-validates from scratch: magic `IUTQ` (`0x49555451`), blob format version, FNV-1a 64 payload hash, section layout consistency, sorted lookup/types, in-bounds clip windows, lane monotonicity, boundary windows re-derived from the clips they index, and directory contiguity. The runtime never redoes any of it.

## Queries — three levels

Resolve once, query every frame:

```cs
DatabaseView db = database.AsView();
ClipQuery<ForceClip> force = db.Query(forceHandle);
```

### Level 1 — raw, maximum control

```cs
foreach (ref readonly TimelineCursor cursor in activeTimelines)
{
    foreach (ref readonly TrackInstance track in force.Tracks(cursor.Timeline))
    {
        int entity = bindingEntity[track.Binding];

        foreach (ClipSample sample in force.Sample(in track, cursor.Tick))
        {
            ref readonly ForceClip clip = ref force.Data(in sample);
            forces[entity] += clip.Forward * sample.Weight;
        }
    }
}
```

`Tracks(timelineIndex)` returns a dense `ReadOnlySpan<TrackInstance>`; `TrackTemplate(in track)` and `Track(in track)` expose the interned shared content (mode, clip slice, lane split); `Sample(in track, tick)` yields at most two 8-byte `ClipSample { DataOffset, Weight }`; `Data(...)` returns the payload by ref. Sampling is direction-free.

The engine never learns what a binding maps to — entity, component index, bone, physics body or network entity. That lookup belongs to the consuming system, which owns the outer loop at this level.

### Level 2 — rich lifecycle frames

```cs
foreach (ClipFrame frame in force.VisitFrames(in track, in cursor))
{
    if (frame.Phase == ClipPhase.Enter) { ... }
}
```

`ClipFrame` carries the clip window, direction, `BlendPhase`, ease, weight and blend factor, plus computed `Phase` (Enter/Active/Exit — reverse-aware, pulse-aware) and eased `Progress`. Fused forms: `VisitFrames(in track, tick, direction, ref visitor)` for one track and `VisitFrames(activeTimelines, ref visitor)` across cursors.

### Level 3 — fused weight-only pass

```cs
public struct ForceAccumulator : IClipSampleVisitor<ForceClip>
{
    public float Forward;

    public void Sample(in TrackInstance track, in ForceClip clip, float weight) =>
        Forward += clip.Forward * weight;
}
```

```cs
force.Sample(activeTimelines, ref accumulator);
```

The fastest common case: track identity + payload + weight, nothing else constructed. Per-track fused form: `Sample(in track, tick, ref visitor)`. Level 3 is an optimized helper and is never required — every level composes with the others.

`activeTimelines` is caller-owned memory — an ECS buffer, native slice, stack span, arena range or any other storage.

## Skipped ticks and rewind

Continuous state uses `Visit` at the current frame. Boundary catch-up uses:

```cs
TimelineSpan movement = new(timeline, previousRawTick, currentRawTick);
query.TraverseTransitions(in movement, ref transitionVisitor);
```

The baker stores a sorted boundary index per interned `TrackTemplate`; traversal binary-searches that immutable window and emits `ClipTransition`/`BlendTransition` occurrences with absolute `GlobalTick`s. Forward and reverse traversal both support looping raw ticks across multiple cycles, and a per-track overload (`TraverseTransitions(in track, in span, ref visitor)`) serves systems that own the outer track loop. One-frame clips therefore replace V2's separate event model while surviving skipped ticks and rewind.

## Removed from V2

```text
IClipPayload / IInterpolator       behavior now lives in the consuming system
IEventPayload / IEventReceiver     replaced by clip transitions
EventHeader                        replaced by TrackBoundary
InstancePool / TimelineInstance    caller-owned TimelineCursor
PlaybackRate / TimelineClock       gameplay/ECS owns rate and local ticks
static TypeSlotCache               explicit ClipTypeHandle, dense directory
byte/ushort count ceilings         natural int indexing; ceiling is the blob
```

## Layout

```
src/Iutq.Core         the engine
tests/Iutq.Tests      xUnit semantic tests
examples/Iutq.Demo    bake -> query demo
bench/Iutq.Bench      BenchmarkDotNet harness
```

## Benchmarks

BenchmarkDotNet, .NET 10, Intel i9-14900K (`bench/Iutq.Bench`). Frame/Sample benchmarks include the per-frame setup (`AsView` + `Query` + `Tracks`). The bench's `GlobalSetup` verifies hand-computed outputs for every scenario before any measurement runs, so a perf run self-aborts on a semantic regression.

| Method                   | Mean      | Allocated |
|--------------------------|----------:|----------:|
| FrameExclusive           |  3.27 ns  | 0 B       |
| FrameCrossFade           |  6.21 ns  | 0 B       |
| SampleExclusive          |  2.89 ns  | 0 B       |
| SampleCrossFade          |  5.49 ns  | 0 B       |
| SampleFusedOneTrack      |  2.99 ns  | 0 B       |
| SampleEightCursors       | 19.25 ns  | 0 B       |
| VisitOneCursor           |  3.82 ns  | 0 B       |
| VisitEightCursors        | 21.17 ns  | 0 B       |
| TraverseForwardOneTick   |  5.39 ns  | 0 B       |
| TraverseForwardFullLoop  |  7.79 ns  | 0 B       |
| TraverseRewindTwentyFive |  8.07 ns  | 0 B       |
| QuerySetupOnly           |  0.87 ns  | 0 B       |

Same machine, V2 (`TimelineEngine`) comparison — steady-state loops with setup hoisted, best of 7, 20M ops, identical clip windows and payloads, Ryzen 5 8500G:

| scenario                              | V2 ns/op | V3 ns/op | V3/V2 |
|--------------------------------------- |---------:|---------:|------:|
| sample exclusive (per sample)         |     3.317 |     4.409 | 1.33  |
| sample normalized / crossfade         |     5.791 |     8.388 | 1.45  |
| full frame per instance/cursor        |     9.277 |     6.353 | 0.69  |
| traverse 64-tick loop span (5 events) |     9.389 |    31.646 | 3.37  |

Read honestly:

- The bare per-sample kernel is ~1.33x V2. V3 stages a `ClipFrame`/`ClipSample` and lets the consuming system compute progress/ease/payload reads; V2 fuses search + ease + interpolation into one call returning the final value. That is the cost of "payload is data, behavior belongs to the system".
- The whole-frame idiom is 31% faster than V2's full instance tick: the type handle is resolved once, the type-major directory is dense integer indexing, and there is no instance pool, no per-instance type-cache traffic and no clock ownership.
- The weight-only Level-3 pass (`SampleEightCursors`, 19.25 ns) edges out the frame-carrying `VisitEightCursors` (21.17 ns) on the same cursor span — the level split pays without giving anything up.
- Transition traversal after the loop-cycle rework (no-division single-cycle fast path, linear scan for short boundary windows) sits at ~8 ns per 64-tick looping span: boundary rows carry clip indices and payload offsets (12 B) and blend emissions reference two payloads, versus V2's minimal event headers. Traversal is catch-up work, not a per-sample path.
- Zero allocations in every engine, every scenario.

## Guarantees

- Zero allocations in every query path (`Visit`, `Frame`, `TraverseTransitions`) — asserted by test and measured in `bench`.
- Deterministic bakes: identical authoring input produces byte-identical blobs.
- Validate once, trust forever: no per-sample checks anywhere in the hot kernel.
- Total functions over partial ones: `Tracks` of an unknown id is an empty span; `VisitFrames` outside every clip is an empty enumerator.
