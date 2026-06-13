using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ParaPerf
{
    // SAFE perf fix #4 — stop CharacterVisual.SetHovered from rebuilding materials when the state didn't change.
    //
    // UpdateHover (a PER-PLAYER system) calls UnHoverEverythingElse every frame, which iterates the FULL loaded
    // roster (~99) calling CharacterVisual.SetHovered(false). SetHovered (CharacterVisual.cs:276) has NO
    // already-in-state guard — it unconditionally does `_skinnedMeshRenderer.materials = new Material[..]{..}`,
    // which both allocates a managed Material[] AND makes Unity instantiate material copies. So every frame:
    // ~99 array allocs + ~99 material reassignments PER PLAYER (≈400/frame in 4-player splitscreen) — steady GC +
    // CPU churn that scales players × roster and janks all viewports at once.
    //
    // Fix: cache the last-applied hovered state per visual; if SetHovered is called with the SAME value, skip the
    // original entirely. The renderer's material state is changed ONLY by SetHovered (and SetMesh, which rebuilds
    // to the unhovered default — handled by the Postfix below resetting the cache), so the cache stays truthful.
    // Behaviour-identical: the visual outcome is the same, we just don't redo work that's already done.
    internal static class HoverChurnPatch
    {
        // Per-visual last-applied hover state. ConditionalWeakTable auto-evicts when a visual is GC'd, and a
        // freshly-built visual starts unhovered (materials = [_material]) which matches the default false.
        private static readonly ConditionalWeakTable<CharacterVisual, StrongBox<bool>> _state =
            new ConditionalWeakTable<CharacterVisual, StrongBox<bool>>();

        [HarmonyPatch(typeof(CharacterVisual), nameof(CharacterVisual.SetHovered))]
        [HarmonyPrefix]
        private static bool SetHoveredPrefix(CharacterVisual __instance, bool hovered)
        {
            try
            {
                if (!Plugin.On(Plugin.HoverChurnSkip)) return true;   // disabled / master off -> vanilla
                StrongBox<bool> box = _state.GetOrCreateValue(__instance);
                if (box.Value == hovered) return false;               // already in this state -> skip the rebuild
                box.Value = hovered;                                  // record, then let the original apply it
            }
            catch { return true; }
            return true;
        }

        // SetMesh rebuilds the renderer's materials to the unhovered default — keep the cache truthful so a
        // re-hover after an appearance change isn't wrongly skipped.
        [HarmonyPatch(typeof(CharacterVisual), nameof(CharacterVisual.SetMesh))]
        [HarmonyPostfix]
        private static void SetMeshPostfix(CharacterVisual __instance)
        {
            try { _state.GetOrCreateValue(__instance).Value = false; }
            catch { }
        }
    }
}
