using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ParaPerf
{
    // This game does NOT call Update()/OnGUI() on BepInEx plugin objects, so we drive our per-frame tick
    // from a Harmony postfix on a game MonoBehaviour that DOES get LateUpdate (the proven trick from
    // ParaController/ParaWASD). Plugin.Tick() spawns the menu on a fresh GameObject whose Update/OnGUI fire.
    [HarmonyPatch(typeof(CursorManager), "LateUpdate")]
    internal static class TickPatch
    {
        private static void Postfix()
        {
            try { Plugin.Instance?.Tick(); } catch { }
        }
    }

    // The '\' debug panel: master kill switch, per-fix toggles, debug options, and an optional FPS overlay.
    public class ParaPerfMenu : MonoBehaviour
    {
        public static bool IsOpen { get; private set; }

        private Rect _rect = new Rect(60f, 60f, 430f, 100f);
        private GUIStyle _windowStyle, _label, _header, _toggle, _overlay, _toast;
        private Texture2D _bgTex, _overlayBgTex, _toastBgTex;
        private float _fps;

        public static void Toggle() => IsOpen = !IsOpen;

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 0f) _fps = (_fps <= 0f) ? (1f / dt) : Mathf.Lerp(_fps, 1f / dt, 0.1f);

            if (IsOpen)   // free the cursor so the panel is clickable
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        private void OnGUI()
        {
            EnsureStyles();

            if (Plugin.ShowPerfOverlay.Value)
            {
                float ms = (_fps > 0f) ? (1000f / _fps) : 0f;
                GUI.Label(new Rect(8f, 8f, 240f, 24f), $"ParaPerf   {_fps:0} fps   {ms:0.0} ms", _overlay);
            }

            DrawActivationToast();

            if (!IsOpen) return;
            _rect = GUILayout.Window(0x50415250 /* 'PARP' */, _rect, Draw, "ParaPerf", _windowStyle);
        }

        // A brief, fading "ParaPerf active" confirmation the first time the game goes in-world.
        private void DrawActivationToast()
        {
            if (!Plugin.ShowActivationToast.Value || !Plugin.Engaged) return;
            float age = Time.unscaledTime - Plugin.EngagedTime;
            if (age > 4.5f) return;

            float alpha = (age < 3.5f) ? 1f : Mathf.Clamp01(4.5f - age);
            float w = 380f, h = 30f;
            Rect r = new Rect((Screen.width - w) * 0.5f, 46f, w, h);
            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Label(r, "ParaPerf active  —  performance fixes engaged", _toast);
            GUI.color = prev;
        }

        private void Draw(int id)
        {
            bool master = Plugin.MasterEnabled.Value;

            string status = Plugin.Engaged ? "ACTIVE — running in-world" : "waiting for game to load…";
            GUILayout.Label($"v{Plugin.PluginVersion}    status: {status}", _label);

            Header("Master");
            BoolRow("ParaPerf ENABLED  (master kill switch)", Plugin.MasterEnabled);
            if (!master) GUILayout.Label("   disabled — every fix below is vanilla", _label);

            GUI.enabled = master;
            Header("Performance fixes");
            BoolRow("Mirror frustum cull", Plugin.MirrorFrustumCull);
            FloatRow("     cull radius", Plugin.MirrorCullRadius, 0.5f, 12f, "m");
            BoolRow("Brain-logic alloc pool  (GC stutter fix)", Plugin.PoolBrainLogicAllocs);
            BoolRow("Reduce log-string GC", Plugin.ReduceLogStringGC);
            GUI.enabled = true;

            Header("Debug");
            BoolRow("FPS / frame-time overlay", Plugin.ShowPerfOverlay);
            BoolRow("Navmesh load-timing log  (Player.log)", Plugin.NavmeshTimingLog);
            float ms = (_fps > 0f) ? (1000f / _fps) : 0f;
            GUILayout.Label($"   now: {_fps:0} fps   /   {ms:0.0} ms/frame", _label);

            Header($"Lag-spike capture   [mark key: {Plugin.LagMarkKey.Value}]");
            BoolRow("Lag capture  (record per-system cost)", Plugin.LagCaptureEnabled);
            BoolRow("   auto-mark big spikes", Plugin.LagAutoMark);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button($"Mark lag spike now  ({Plugin.LagMarkKey.Value})")) LagCapture.Mark("manual");
            GUILayout.EndHorizontal();
            GUILayout.Label($"   markers: {LagCapture.MarkerCount}   last: {LagCapture.LastMarker}", _label);
            if (!Plugin.LagCaptureEnabled.Value)
                GUILayout.Label("   (enable Lag capture to attribute spikes to a system)", _label);

            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset to defaults")) ResetDefaults();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Width(90f))) IsOpen = false;
            GUILayout.EndHorizontal();

            GUILayout.Label($"toggle key: {Plugin.MenuKey.Value}   (change in config)", _label);
            GUI.DragWindow(new Rect(0f, 0f, 100000f, 22f));
        }

        private static void ResetDefaults()
        {
            SetDef(Plugin.MasterEnabled);
            SetDef(Plugin.MirrorFrustumCull);
            Plugin.MirrorCullRadius.Value = (float)Plugin.MirrorCullRadius.DefaultValue;
            SetDef(Plugin.PoolBrainLogicAllocs);
            SetDef(Plugin.ReduceLogStringGC);
            SetDef(Plugin.ShowPerfOverlay);
            SetDef(Plugin.NavmeshTimingLog);
        }

        private static void SetDef(ConfigEntry<bool> e) => e.Value = (bool)e.DefaultValue;

        // ---- IMGUI helpers ------------------------------------------------------------------------
        private void Header(string t)
        {
            GUILayout.Space(6f);
            GUILayout.Label(t, _header);
        }

        private void BoolRow(string label, ConfigEntry<bool> e)
        {
            bool v = GUILayout.Toggle(e.Value, "  " + label, _toggle);
            if (v != e.Value) e.Value = v;
        }

        private void FloatRow(string label, ConfigEntry<float> e, float min, float max, string unit)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _label, GUILayout.Width(140f));
            float v = GUILayout.HorizontalSlider(e.Value, min, max, GUILayout.Width(150f));
            if (!Mathf.Approximately(v, e.Value)) e.Value = v;
            GUILayout.Label($"{e.Value:0.0} {unit}", _label, GUILayout.Width(60f));
            GUILayout.EndHorizontal();
        }

        private void EnsureStyles()
        {
            if (_windowStyle != null) return;

            _bgTex = Solid(new Color(0.08f, 0.09f, 0.12f, 0.94f));
            _overlayBgTex = Solid(new Color(0f, 0f, 0f, 0.5f));

            _windowStyle = new GUIStyle(GUI.skin.window);
            _windowStyle.normal.background = _bgTex;
            _windowStyle.onNormal.background = _bgTex;
            _windowStyle.normal.textColor = Color.white;
            _windowStyle.onNormal.textColor = Color.white;

            _label = new GUIStyle(GUI.skin.label) { wordWrap = false };
            _label.normal.textColor = new Color(0.85f, 0.87f, 0.9f);

            _header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            _header.normal.textColor = new Color(0.66f, 0.83f, 1f);

            _toggle = new GUIStyle(GUI.skin.toggle);
            _toggle.normal.textColor = Color.white;
            _toggle.onNormal.textColor = Color.white;
            _toggle.hover.textColor = Color.white;
            _toggle.onHover.textColor = Color.white;

            _overlay = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, padding = new RectOffset(6, 6, 3, 3) };
            _overlay.normal.textColor = new Color(0.6f, 1f, 0.7f);
            _overlay.normal.background = _overlayBgTex;

            _toastBgTex = Solid(new Color(0.10f, 0.16f, 0.12f, 0.92f));
            _toast = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(10, 10, 6, 6)
            };
            _toast.normal.textColor = new Color(0.55f, 1f, 0.7f);
            _toast.normal.background = _toastBgTex;
        }

        private static Texture2D Solid(Color c)
        {
            Texture2D t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }
    }
}
