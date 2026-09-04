# iutq

**I**mmutable **U**nique **T**imeline **Q**uery — V3 of the timeline engine lineage (`TimelineEngine` V1/V2 → `iutq`).

Pure .NET 10. One immutable blob, stateless queries, zero allocations at steady state. No source generators, no wrapper cascades, no runtime playback ownership.

## Runtime model

The engine does not own playback instances. Gameplay/ECS owns the active timeline cursors:

```cs
public readonly struct TimelineCursor
{
    public readonly TimelineId Timeline;
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

There is no runtime `TimelineInstance`, no core timeline pool, no lock, no per-frame hit array and no global maximum active-timeline count.

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
  TrackDataId
        |
        v
TrackData              interned shared content
  TypeKey
  Clip window
  Lane split
  Mode
  Boundary window
        |
        +----> ClipHeader[]
        |
        +----> BoundaryHeader[]
        |
        +----> interned payload arena
```

Structural deduplication: 10,000 enemies playing the same attack share one interned clip window, one `TrackData` and one set of payloads. Each pays only a 8-byte `TrackInstance` row.

## Lifecycle semantics

Clips use `[Start, End)` storage. For `[10, 15)` the active ticks are `10..14`.

```text
Forward:  10 Enter, 11 Stay, 12 Stay, 13 Stay, 14 Exit
Reverse:  14 Enter, 13 Stay, 12 Stay, 11 Stay, 10 Exit
```

A one-frame `[10, 11)` clip is a pulse: `Enter` in both directions, never `Exit`. Paused multi-frame clips report `Stay`; paused one-frame pulses report `None` so the pulse is not retriggered every frame.

## Crossfade semantics

`TrackMode.CrossFade` is explicitly pairwise — the baker rejects a third simultaneous clip. For two active clips each `ClipHit` carries a normalized `Weight` and the current `BlendPhase`. The same baked overlap runs backward as the tick decreases; no separate reverse data set exists.

## Type-major database layout

```text
Directory[typeSlot * timelineCount + timelineId] -> TypePartition { TrackStart, TrackCount }
```

A system resolves its clip type once:

```cs
ClipHandle<ForceClip> forceHandle = database.Resolve(GameClipTypes.Force);
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
TimelineId attack = builder.AddTimeline(new TimelineKey(1), 30);

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

Bake-time work: payload interning, clip-window interning, `TrackData` interning, deterministic type/timeline partitioning, pairwise crossfade validation, lifecycle/blend boundary baking, blob assembly, structural validation and payload hashing. `Build()` runs the same full validator as `Load()`; after construction the database is proof that the invariants hold and the query kernel trusts it.

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

`Tracks(timelineId)` returns a dense `ReadOnlySpan<TrackInstance>`; `TrackData(in track)` and `Track(in track)` expose the interned shared content (mode, clip window, lane split); `Sample(in track, tick)` yields at most two 8-byte `ClipSample { DataOffset, Weight }`; `Data(...)` returns the payload by ref. Sampling is direction-free.

The engine never learns what a binding maps to — entity, component index, bone, physics body or network entity. That lookup belongs to the consuming system, which owns the outer loop at this level.

### Level 2 — rich lifecycle frames

```cs
foreach (ClipHit hit in force.Frame(in track, in cursor))
{
    if (hit.Phase == ClipPhase.Enter) { ... }
}
```

`ClipHit` carries the clip window, direction, `BlendPhase`, ease, weight and blend factor, plus computed `Phase` (Enter/Stay/Exit — reverse-aware, pulse-aware) and eased `Progress`. Fused forms: `Frame(in track, tick, direction, ref visitor)` for one track and `Visit(activeTimelines, ref visitor)` across cursors.

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

The baker stores a sorted boundary index per interned `TrackData`; traversal binary-searches that immutable window and emits `ClipTransition`/`BlendTransition` occurrences with absolute `OccurrenceTick`s. Forward and reverse traversal both support looping raw ticks across multiple cycles, and a per-track overload (`TraverseTransitions(in track, in span, ref visitor)`) serves systems that own the outer track loop. One-frame clips therefore replace V2's separate event model while surviving skipped ticks and rewind.

## Removed from V2

```text
IClipPayload / IInterpolator       behavior now lives in the consuming system
IEventPayload / IEventReceiver     replaced by clip transitions
EventHeader                        replaced by BoundaryHeader
InstancePool / TimelineInstance    caller-owned TimelineCursor
PlaybackRate / TimelineClock       gameplay/ECS owns rate and local ticks
static TypeSlotCache               explicit ClipHandle, dense directory
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

BenchmarkDotNet, .NET 10, Ryzen 5 8500G (`bench/Iutq.Bench`). Frame/Sample benchmarks include the per-frame setup (`AsView` + `Query` + `Tracks`):

| Method                   | Mean      | Allocated |
|-------------------------- |----------:|----------:|
| FrameExclusive           | 16.01 ns  | 0 B       |
| FrameCrossFade           | 20.26 ns  | 0 B       |
| SampleExclusive          | 15.96 ns  | 0 B       |
| SampleCrossFade          | 19.48 ns  | 0 B       |
| SampleFusedOneTrack      | 17.46 ns  | 0 B       |
| SampleEightCursors       | 44.07 ns  | 0 B       |
| VisitOneCursor           | 17.95 ns  | 0 B       |
| VisitEightCursors        | 45.22 ns  | 0 B       |
| TraverseForwardOneTick   | 37.56 ns  | 0 B       |
| TraverseForwardFullLoop  | 49.66 ns  | 0 B       |
| TraverseRewindTwentyFive | 48.45 ns  | 0 B       |
| QuerySetupOnly           | 11.65 ns  | 0 B       |

Same machine, V2 (`TimelineEngine`) comparison — steady-state loops with setup hoisted, best of 7, 20M ops, identical clip windows and payloads:

| scenario                              | V2 ns/op | V3 ns/op | V3/V2 |
|--------------------------------------- |---------:|---------:|------:|
| sample exclusive (per sample)         |     3.317 |     4.409 | 1.33  |
| sample normalized / crossfade         |     5.791 |     8.388 | 1.45  |
| full frame per instance/cursor        |     9.277 |     6.353 | 0.69  |
| traverse 64-tick loop span (5 events) |     9.389 |    31.646 | 3.37  |

Read honestly:

- The bare per-sample kernel is ~1.33x V2. V3 stages a `ClipHit`/`ClipSample` and lets the consuming system compute progress/ease/payload reads; V2 fuses search + ease + interpolation into one call returning the final value. That is the cost of "payload is data, behavior belongs to the system".
- The whole-frame idiom is 31% faster than V2's full instance tick: the type handle is resolved once, the type-major directory is dense integer indexing, and there is no instance pool, no per-instance type-cache traffic and no clock ownership.
- The weight-only Level-3 pass (`SampleEightCursors`, 44.07 ns) edges out the hit-carrying `VisitEightCursors` (45.22 ns) on the same cursor span — the level split pays without giving anything up.
- Transition traversal is ~32 ns absolute per 64-tick looping span but ~3.4x V2's dedicated event rows: boundary rows carry clip indices and payload offsets (12 B) and blend emissions reference two payloads, versus V2's minimal event headers. Traversal is catch-up work, not a per-sample path.
- Zero allocations in every engine, every scenario.

## Guarantees

- Zero allocations in every query path (`Visit`, `Frame`, `TraverseTransitions`) — asserted by test and measured in `bench`.
- Deterministic bakes: identical authoring input produces byte-identical blobs.
- Validate once, trust forever: no per-sample checks anywhere in the hot kernel.
- Total functions over partial ones: `Tracks` of an unknown id is an empty span; `Frame` outside every clip is an empty enumerator.
