using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Setting;
using UnityEngine;

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
        public const string PluginVersion = "0.7.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        // Set once the game is actually in-world (the patches apply at startup but only DO anything once the
        // sim/mirrors/brain-logic run — so this is the moment the mod visibly "kicks in"). Drives the toast.
        internal static bool Engaged;
        internal static float EngagedTime;

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

        // --- Hover material churn (SAFE) ----------------------------------------------------------
        // UpdateHover (per-player) clears hover on the whole roster every frame; CharacterVisual.SetHovered
        // rebuilds the renderer's materials with no already-in-state guard (~99 Material[] allocs +
        // reassignments per player per frame). We skip the rebuild when the hover state is unchanged.
        internal static ConfigEntry<bool> HoverChurnSkip;

        // --- Status-effect value GC (SAFE) --------------------------------------------------------
        // StatusEffectManager.GetStatusEffectValueForEffectType allocates a throwaway List every call (need +
        // skill reads, every frame across the roster). We compute the value inline with no list.
        internal static ConfigEntry<bool> TrimStatusEffectGC;

        // --- Menu + master switch -----------------------------------------------------------------
        // A small in-game panel (default key '\') with a master kill switch, per-fix toggles, and
        // debug options. Key is configurable because '\' collides with ParaController/ParaGaze panels.
        internal static ConfigEntry<bool> MasterEnabled;
        internal static ConfigEntry<KeyCode> MenuKey;
        internal static ConfigEntry<bool> ShowPerfOverlay;
        internal static ConfigEntry<bool> NavmeshTimingLog;
        internal static ConfigEntry<bool> ShowActivationToast;

        // --- Lag-spike capture --------------------------------------------------------------------
        // Continuously ring-buffers recent frames; the mark key (default K) bookmarks the worst recent
        // frame + the systems that caused it to BepInEx/ParaPerf/lag-markers.log for after-the-fact study.
        internal static ConfigEntry<bool> LagCaptureEnabled;
        internal static ConfigEntry<bool> LagAutoMark;
        internal static ConfigEntry<KeyCode> LagMarkKey;

        // Every fix checks this: a fix runs only if the master switch AND its own toggle are on.
        internal static bool On(ConfigEntry<bool> fix) => MasterEnabled.Value && fix.Value;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

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

            HoverChurnSkip = Config.Bind("Allocations", "HoverChurnSkip", true,
                "SAFE. The hover system rebuilds every character's outline materials each frame even when nothing " +
                "is hovered (~99 material-array allocations + reassignments per player per frame). This skips the " +
                "rebuild when a character's hover state hasn't changed. Especially impactful in splitscreen (the " +
                "cost is per-player). Behaviour-identical.");

            TrimStatusEffectGC = Config.Bind("Allocations", "TrimStatusEffectGC", true,
                "SAFE. Status-effect value lookups (needs + skills, read every frame for the whole roster) allocate " +
                "a throwaway List on every call. This computes the value inline with no allocation — same result, " +
                "less GC. Helps frame consistency, especially in splitscreen where a GC pause stalls both views.");

            MasterEnabled = Config.Bind("General", "MasterEnabled", true,
                "Master switch for ALL ParaPerf fixes. Uncheck to make the whole mod inert (every fix reverts to " +
                "vanilla) without uninstalling — useful for A/B perf testing. Also toggleable in the '\\' menu.");
            MenuKey = Config.Bind("General", "MenuKey", KeyCode.Backslash,
                "Key that opens the in-game ParaPerf panel. Change it if it clashes with another mod's panel " +
                "(ParaController and ParaGaze also use '\\').");
            ShowPerfOverlay = Config.Bind("Debug", "ShowPerfOverlay", false,
                "Show a small on-screen FPS / frame-time readout (top-left).");
            ShowActivationToast = Config.Bind("General", "ShowActivationToast", true,
                "Briefly show an on-screen 'ParaPerf active' confirmation the first time the game goes in-world " +
                "(the patches load at startup but only take effect once the sim is running). Set false to hide it.");
            NavmeshTimingLog = Config.Bind("Debug", "NavmeshTimingLog", false,
                "Enable the game's own navmesh-rebuild timing log (writes to Player.log) — shows how long lot/" +
                "terrain ('dirty path') navmesh rebuilds take during a load. Diagnostic only; off by default.");

            LagCaptureEnabled = Config.Bind("Debug", "LagCapture", false,
                "Record recent frames + per-system cost so a lag spike can be bookmarked and investigated. " +
                "Has a small observer cost (turns on the game's per-system profiler), so it's opt-in. Frame " +
                "times are always tracked; this adds the which-system attribution.");
            LagAutoMark = Config.Bind("Debug", "LagAutoMark", false,
                "While Lag capture is on, automatically bookmark any frame over 50ms AND 4x the recent norm " +
                "(rate-limited to once/sec) — catches spikes you don't react to.");
            LagMarkKey = Config.Bind("Debug", "LagMarkKey", KeyCode.K,
                "Press to bookmark the worst recent frame to BepInEx/ParaPerf/lag-markers.log. Press it right " +
                "after you feel a lag spike — the recent ring buffer still holds it.");

            try
            {
                LagCapture.LogPath = Path.Combine(Paths.BepInExRootPath, "ParaPerf", "lag-markers.log");
                Directory.CreateDirectory(Path.GetDirectoryName(LagCapture.LogPath));
            }
            catch (System.Exception e) { Logger.LogWarning("ParaPerf: lag-marker path setup failed: " + e.Message); }

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

        // This game does NOT call Update() on BepInEx plugins, so the real driver is a Harmony postfix
        // on CursorManager.LateUpdate (see TickPatch in Menu.cs). Update() is kept as a harmless backup;
        // the per-frame debounce stops any double-toggle.
        private void Update() => Tick();

        private bool _menuSpawned;
        private long _lastMenuKeyFrame = -1L;
        private long _lastMarkKeyFrame = -1L;

        internal void Tick()
        {
            try
            {
                // The menu MonoBehaviour must live on a GameObject spawned from the LIVE loop — objects
                // created during the BepInEx bootstrap don't tick, but ones created here (and their
                // Update()/OnGUI()) do.
                if (!_menuSpawned)
                {
                    _menuSpawned = true;
                    GameObject go = new GameObject("ParaPerf.Menu");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    go.AddComponent<ParaPerfMenu>();
                    go.AddComponent<LagCapture>();
                    Log.LogInfo("Spawned ParaPerf menu + lag-capture.");
                }

                // The patches apply at startup but are inert until the game is in-world — detect that moment
                // so the menu can flash a "ParaPerf active" confirmation (the "OK now it's working" signal).
                if (!Engaged)
                {
                    SavedGameManager sgm = SavedGameManager.Instance;
                    if (SystemManager.Instance != null && sgm != null && sgm.IsGameLoaded)
                    {
                        Engaged = true;
                        EngagedTime = Time.unscaledTime;
                        Log.LogInfo("ParaPerf engaged — performance fixes are now active in-world.");
                    }
                }

                if (Input.GetKeyDown(MenuKey.Value) && Time.frameCount != _lastMenuKeyFrame)
                {
                    _lastMenuKeyFrame = Time.frameCount;
                    ParaPerfMenu.Toggle();
                }

                if (Input.GetKeyDown(LagMarkKey.Value) && Time.frameCount != _lastMarkKeyFrame)
                {
                    _lastMarkKeyFrame = Time.frameCount;
                    LagCapture.Mark("manual");
                }

                // Keep the game's navmesh timing log in sync with our debug toggle (cheap; only writes on change).
                Loggers loggers = Settings.Get<Loggers>();
                if (loggers != null && loggers.LogPathfindingManager != NavmeshTimingLog.Value)
                    loggers.LogPathfindingManager = NavmeshTimingLog.Value;
            }
            catch { }
        }
    }
}
