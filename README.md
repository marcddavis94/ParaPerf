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

### 4. Hover material churn  *(`Allocations/HoverChurnSkip`, default on)*
The hover system rebuilds every character's outline materials each frame even when nothing is hovered — about
99 material-array allocations + Unity material reassignments **per player** per frame. This skips the rebuild
when a character's hover state hasn't changed. Behaviour-identical, and especially impactful in splitscreen
(the cost is per-player).

### 5. Status-effect value GC  *(`Allocations/TrimStatusEffectGC`, default on)*
Status-effect value lookups (needs + skills, read every frame across the whole roster) allocated a throwaway
`List` on every call. ParaPerf computes the value inline with no allocation — same result, less GC. Helps frame
consistency, especially in splitscreen where a GC pause stalls both views.

> Fixes #4 and #5 came from a multi-agent audit of the game files plus live 2-player profiling, aimed at
> hardening splitscreen/co-op: a GC pause janks *every* viewport at once, so trimming per-frame allocations is
> the simulation-side stability lever (rendering each viewport is GPU-bound and outside a mod's reach).

## In-game panel & tools

Press **`\`** (configurable — `General/MenuKey`) to open the ParaPerf panel:

- **Master kill switch** — turn the whole mod inert (every fix reverts to vanilla) without uninstalling, handy
  for A/B perf testing.
- **Per-fix toggles** — mirror cull (+ a cull-radius slider), brain-logic alloc pool, log-string GC. A fix runs
  only if the master switch *and* its own toggle are on.
- **Debug** — an FPS / frame-time overlay, and the game's own navmesh load-timing log.

An **"ParaPerf active"** toast confirms when the fixes go live (they load at startup but only take effect once
you're in-world; the toast marks that moment). Toggle it with `General/ShowActivationToast`.

### Lag-spike bookmarker

Hit something stuttery? Press **`K`** (configurable). ParaPerf continuously ring-buffers the last few seconds of
frames, so the key bookmarks the **worst recent frame** — its time, how many × the norm it was, and **which systems
ate it** — to `BepInEx/ParaPerf/lag-markers.log`. Enable **Lag capture** in the panel first for per-system
attribution (it has a small profiler cost, so it's opt-in; pressing `K` auto-enables it). There's also an
**auto-mark** option that bookmarks big spikes on its own. Great for diagnosing — and for sending the author a
concrete marker log instead of "it lags sometimes."

> Note: `\` (and `K`) may collide with other mods' panels — both are rebindable in the config.

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
