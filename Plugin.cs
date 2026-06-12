using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ParaPerf
{
    // ParaPerf — a performance mod for Paralives. Targeted, measured fixes for the per-frame hot paths
    // found with the game's own per-system profiler (SystemManager) via ParaScope's `perftop` bridge.
    // Philosophy: SAFE (behaviour-identical) fixes on by default; anything that changes sim timing is
    // opt-in and validated. Every patch is wrapped so a mod bug can never crash the game.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.marcusdavis2012.paraperf";
        public const string PluginName = "ParaPerf";
        public const string PluginVersion = "0.3.0";

        internal static ManualLogSource Log;

        // --- Mirrors (SAFE) -----------------------------------------------------------------------
        // The #1 sim cost (~24% of sim CPU + a full extra GPU render pass). The game already skips
        // mirrors the camera is behind, but renders any mirror it FACES even when off-screen. We add
        // the missing frustum cull. Off-screen mirrors auto-unregister (game lifecycle) and re-render
        // on return, so anything you can see is unchanged.
        internal static ConfigEntry<bool> MirrorFrustumCull;
        internal static ConfigEntry<float> MirrorCullRadius;

        // --- Log-string GC (SAFE) -----------------------------------------------------------------
        // Per-frame evaluators / brain logic build eager log strings (StaticLoggerUtils.GetUrledMsg wraps
        // every message in <a href="<dev file path>"...> markup) that are immediately discarded in normal
        // play. Live profiling showed ~205 KB/frame of this in UpdateCharacterMemories alone. We return the
        // message unwrapped — the discarded-string content is preserved, only the IDE-link markup is dropped.
        internal static ConfigEntry<bool> ReduceLogStringGC;

        // --- Brain-logic allocation pooling (SAFE, algorithm-preserving) ---------------------------
        // The measured stutter source: BrainLogicManager.Process / GetProcessedOutcomes allocate throwaway
        // scratch lists every frame for every visual-loaded Para (~205-226 KB/frame). A transpiler pools
        // those lists without touching the interpreter's logic. Disable to A/B or if any sim oddity appears.
        internal static ConfigEntry<bool> PoolBrainLogicAllocs;

        private void Awake()
        {
            Log = Logger;

            MirrorFrustumCull = Config.Bind("Mirrors", "FrustumCull", true,
                "SAFE. Skip rendering a mirror's reflection when the mirror is outside the camera's view " +
                "(off-screen / behind a wall). The game already culls mirrors you're behind; this adds the " +
                "missing on-screen test. Reclaims the biggest single sim cost when no mirror is in view.");
            MirrorCullRadius = Config.Bind("Mirrors", "CullRadius", 4.0f,
                "Generous radius (metres) of the sphere tested against the camera frustum, centred on the " +
                "mirror. Larger = more conservative (renders mirrors slightly off-screen too). Lower only if " +
                "you see mirrors fail to update at screen edges.");

            ReduceLogStringGC = Config.Bind("Allocations", "ReduceLogStringGC", true,
                "SAFE. The game's requirement evaluators and brain logic eagerly build clickable-source-link " +
                "log strings every frame (then discard them in normal play) — measured at ~205 KB/frame of " +
                "garbage in the character-memory system alone. This returns the log message without the dev " +
                "file-path/markup wrapper, cutting that GC churn. The game's own logging (if you enable it) " +
                "still gets the full message text, just without the editor link markup.");

            PoolBrainLogicAllocs = Config.Bind("Allocations", "PoolBrainLogicAllocs", true,
                "SAFE (algorithm-preserving). Pools the throwaway scratch lists that BrainLogicManager allocates " +
                "every frame for every Para's brain logic (the measured ~205-226 KB/frame GC-stutter source). " +
                "Uses a Harmony transpiler that swaps only the allocation instructions — the interpreter's logic " +
                "is byte-identical. If you ever notice Paras behaving oddly, set this false and report it.");

            try
            {
                new Harmony(PluginGuid).PatchAll();
                Log.LogInfo($"{PluginName} v{PluginVersion} loaded. Patches applied.");
            }
            catch (System.Exception e)
            {
                Log.LogError($"{PluginName} failed to apply patches: {e}");
            }
        }
    }
}
