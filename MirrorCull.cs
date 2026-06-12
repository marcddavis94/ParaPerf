using HarmonyLib;
using UnityEngine;

namespace ParaPerf
{
    // SAFE perf fix #1 — frustum-cull mirror reflection renders.
    //
    // UpdateItemMirrors (a per-frame ParaSystemBase) was the #1 sim cost measured live (1.3-2.5 ms/frame,
    // ~a quarter of all sim CPU) plus a full extra scene render to a RenderTexture (Camera.Render) per mirror.
    // MirrorManager.UpdateMirrorRender already skips mirrors the camera is BEHIND, but it still renders any
    // mirror the camera FACES even if that mirror is off-screen or occluded by a wall. We add the missing
    // frustum test: if the mirror's bounding sphere is outside the player camera frustum, skip the render.
    //
    // Why this is safe: a mirror not rendered for >3 frames is auto-unregistered by the game (MirrorManager.
    // UpdateMirrorRegistration frees its RenderTexture) and re-registers + re-renders when it returns to view
    // — the SAME lifecycle the game already uses for the distance/facing cases. So any mirror you can actually
    // see behaves identically; we only stop rendering reflections you can't see.
    [HarmonyPatch(typeof(MirrorManager), nameof(MirrorManager.UpdateMirrorRender))]
    internal static class MirrorFrustumCullPatch
    {
        // Reused across calls so the frustum test allocates nothing (we are a PERF mod — adding GC here
        // would be self-defeating). CalculateFrustumPlanes(Camera, Plane[]) fills this in place.
        private static readonly Plane[] _planes = new Plane[6];

        // Prefix params bind by name to the original (int itemInstanceID, int playerIndex, Material material,
        // Vector3 itemPosition, Vector3 itemForwardDirection). Return false = skip the original render.
        private static bool Prefix(int playerIndex, Vector3 itemPosition)
        {
            try
            {
                if (!Plugin.On(Plugin.MirrorFrustumCull)) return true;   // disabled (or master off) → vanilla

                PlayerManager pm = PlayerManager.Instance;
                if (pm == null) return true;
                HybridPlayer hp = pm.GetHybridPlayer(playerIndex);
                Camera cam = (hp != null && hp.HybridCamera != null) ? hp.HybridCamera.Camera : null;
                if (cam == null) return true;                       // can't test → render (be safe)

                GeometryUtility.CalculateFrustumPlanes(cam, _planes);
                float r = Plugin.MirrorCullRadius.Value;
                if (r < 0.5f) r = 0.5f;
                Bounds bounds = new Bounds(itemPosition, new Vector3(r * 2f, r * 2f, r * 2f));
                if (!GeometryUtility.TestPlanesAABB(_planes, bounds))
                    return false;                                   // mirror off-screen → skip the reflection
            }
            catch
            {
                return true;   // never let a perf optimisation break the game
            }
            return true;
        }
    }
}
