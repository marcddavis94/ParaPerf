# ParaPerf

A performance mod for **Paralives** (BepInEx 5 / Harmony). Targeted, *measured* fixes for the
per-frame hot paths in the simulation — found with the game's **own** per-system profiler
(`SystemManager`) rather than guesswork.

**Philosophy:** every fix is behaviour-identical (or as close as measurable), on by default, individually
toggleable, and wrapped so a mod bug can never crash the game. Anything that could change sim behaviour is
algorithm-preserving and validated live before shipping.

## What it does

All numbers below were measured live in a 99-Para town (single-player) via the game's per-system profiler.

### 1. Mirror frustum cull  *(`Mirrors/FrustumCull`, default on)*
Mirror reflections are rendered with a full extra scene pass (`Camera.Render` to a RenderTexture). The game
already skips mirrors the camera is *behind*, but still renders any mirror it *faces* — even when that mirror
is off-screen or behind a wall. `UpdateItemMirrors` was the single biggest sim cost measured (~1.3–2.5 ms/frame,
roughly a quarter of all sim CPU) **plus** a whole GPU render pass.

ParaPerf adds the missing on-screen test: if a mirror's bounding sphere is outside the camera frustum, its
reflection isn't rendered. Off-screen mirrors auto-unregister and re-render the instant they return to view —
the same lifecycle the game already uses — so any mirror you can actually see is unchanged.

### 2. Brain-logic allocation pooling  *(`Allocations/PoolBrainLogicAllocs`, default on)*
`UpdateCharacterMemories` runs brain logic for every visual-loaded Para every frame, and `BrainLogicManager`
allocated a pile of throwaway scratch lists on every call (`new List<…>()` + `GetRange` per IF/ELSE/PICK
branch). Measured at **~226 KB/frame** of garbage — constant gen-0 GC, i.e. micro-stutter.

ParaPerf pools those lists with a Harmony **transpiler** that swaps *only* the allocation instructions and
leaves the interpreter's control flow and index math byte-identical (so it can't change which outcomes fire).
A Prefix/Finalizer scopes the pool exception-safely, and pooling only activates inside the brain-logic path,
so external callers stay vanilla. **Result: ~226 KB → ~82 KB/frame (−64%)**, validated live with no behaviour
change.

### 3. Reduced eager log-string allocation  *(`Allocations/ReduceLogStringGC`, default on)*
The game's dev-logging helpers wrap messages in clickable-source-link markup containing a hardcoded dev file
path. Where those are built eagerly (then discarded in shipping), ParaPerf returns the message unwrapped. Minor,
fully safe — the message content is preserved, only the editor-link markup is dropped.

## Install

1. Install [BepInEx 5 (x64)](https://github.com/BepInEx/BepInEx/releases) into your Paralives folder.
2. Drop `ParaPerf.dll` into `Paralives/BepInEx/plugins/ParaPerf/`.
3. Launch the game. Settings live in `Paralives/BepInEx/config/com.marcusdavis2012.paraperf.cfg` after first run.

Every fix has its own toggle in that config file. If anything ever looks off, flip the relevant toggle to
`false` for byte-for-byte vanilla behaviour.

## Build

```
dotnet build -c Release
```

Requires the Paralives managed assemblies + BepInEx core (paths in `ParaPerf.csproj`; defaults assume the
Steam install location). The build auto-deploys the DLL to the game's plugins folder.

## How the fixes were found

The wins came from measurement, not reading code: the game ships an unused per-system profiler
(`SystemManager.CalculateSystemExecutionTimes` / `SystemExecutionTimeInTicks` / `SystemMemoryAllocation`),
surfaced live, ranked the systems by CPU and allocation, and each candidate fix was A/B-measured against the
real numbers before and after.

## License

MIT
