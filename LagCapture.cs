using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace ParaPerf
{
    // Lag-spike bookmarking. A lag spike is over within a frame or two — long before you can react and
    // press the key — so we can't just snapshot "now". Instead we continuously ring-buffer the last ~5s of
    // frames (frame time + the top systems that frame, read from the game's SystemManager profiler), and the
    // marker key dumps the WORST recent frame plus context to an append-only log. Then the spike can be
    // investigated after the fact: which system ate that frame, how big, how it compares to the norm.
    //
    // Frame TIMES are always recorded (negligible cost). Per-SYSTEM attribution requires the game profiler,
    // which the "Lag capture" toggle turns on (it has a small observer cost, so it's opt-in).
    public class LagCapture : MonoBehaviour
    {
        private const int N = 300;          // ~5s at 60fps
        private const int TOP = 5;          // systems recorded per frame

        private static readonly float[] _ms = new float[N];
        private static readonly float[] _simMs = new float[N];
        private static readonly Type[] _topT = new Type[N * TOP];
        private static readonly float[] _topMs = new float[N * TOP];
        private static int _head, _count;

        private static readonly Type[] _scratchT = new Type[TOP];
        private static readonly float[] _scratchMs = new float[TOP];

        private static bool _enabledProfiler;
        private static float _ema = -1f;
        private static float _lastAutoMark;

        internal static string LogPath;
        internal static int MarkerCount;
        internal static string LastMarker = "(none yet)";

        private void LateUpdate()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            int slot = _head;
            _ms[slot] = ms;

            SystemManager sm = SystemManager.Instance;
            bool want = Plugin.LagCaptureEnabled.Value;
            if (sm != null)
            {
                if (want && !_enabledProfiler) { sm.CalculateSystemExecutionTimes = true; _enabledProfiler = true; }
                else if (!want && _enabledProfiler) { sm.CalculateSystemExecutionTimes = false; _enabledProfiler = false; }
            }

            for (int j = 0; j < TOP; j++) { _scratchT[j] = null; _scratchMs[j] = 0f; }
            float sim = 0f;
            if (want && sm != null && sm.SystemExecutionTimeInTicks != null && sm.SystemExecutionTimeInTicks.Count > 0)
            {
                foreach (KeyValuePair<Type, long> kv in sm.SystemExecutionTimeInTicks)   // struct enumerator → no alloc
                {
                    float s = kv.Value / 10000f;   // game convention: ticks/10000 = ms
                    sim += s;
                    Insert(kv.Key, s);
                }
            }
            _simMs[slot] = sim;
            for (int j = 0; j < TOP; j++) { _topT[slot * TOP + j] = _scratchT[j]; _topMs[slot * TOP + j] = _scratchMs[j]; }

            _head = (_head + 1) % N;
            if (_count < N) _count++;

            // auto-mark a genuine spike (rate-limited), if enabled
            if (want && Plugin.LagAutoMark.Value && _count > 30 && _ema > 0f
                && ms > 50f && ms > 4f * _ema && Time.unscaledTime - _lastAutoMark > 1f)
            {
                _lastAutoMark = Time.unscaledTime;
                Mark("auto");
            }
            _ema = (_ema <= 0f) ? ms : Mathf.Lerp(_ema, ms, 0.05f);
        }

        // Keep the TOP-N largest of this frame in the scratch buffers, no allocation.
        private static void Insert(Type t, float ms)
        {
            int minI = 0;
            for (int i = 1; i < TOP; i++) if (_scratchMs[i] < _scratchMs[minI]) minI = i;
            if (ms > _scratchMs[minI]) { _scratchMs[minI] = ms; _scratchT[minI] = t; }
        }

        // Dump the worst recent frame + context to the marker log.
        internal static void Mark(string reason)
        {
            try
            {
                if (_count == 0) { Plugin.Log?.LogInfo("[LagCapture] nothing recorded yet."); return; }
                int oldest = (_count < N) ? 0 : _head;

                int spikePos = 0; float spikeMs = -1f;
                for (int i = 0; i < _count; i++)
                {
                    float v = _ms[(oldest + i) % N];
                    if (v > spikeMs) { spikeMs = v; spikePos = i; }
                }
                int spike = (oldest + spikePos) % N;

                float[] sorted = new float[_count];
                for (int i = 0; i < _count; i++) sorted[i] = _ms[(oldest + i) % N];
                Array.Sort(sorted);
                float median = sorted[_count / 2];

                MarkerCount++;
                StringBuilder sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("================================================================");
                sb.AppendLine($"LAG MARKER #{MarkerCount}  ({reason})   game-frame {Time.frameCount}");
                sb.AppendLine($"  worst recent frame : {spikeMs:0.0} ms   ({(spikeMs > 0 ? 1000f / spikeMs : 0):0} fps that frame)");
                sb.AppendLine($"  recent median      : {median:0.0} ms   ({(median > 0 ? 1000f / median : 0):0} fps typical)   -> spike is {(median > 0 ? spikeMs / median : 0):0.0}x normal");
                int chars = (CharacterManager.Instance != null && CharacterManager.Instance.Characters != null) ? CharacterManager.Instance.Characters.Count : -1;
                sb.AppendLine($"  context            : {chars} Paras,  sim-CPU that frame = {_simMs[spike]:0.0} ms");

                bool hasSystems = _topT[spike * TOP] != null;
                if (hasSystems)
                {
                    sb.AppendLine("  top systems in the spike frame:");
                    for (int j = 0; j < TOP; j++)
                    {
                        Type ty = _topT[spike * TOP + j];
                        if (ty != null) sb.AppendLine($"      {_topMs[spike * TOP + j],7:0.00} ms   {ty.Name}");
                    }
                }
                else
                {
                    sb.AppendLine("  (per-system data not captured — enable 'Lag capture' to attribute the spike to a system)");
                }

                // frame-time context around the spike (chronological)
                int from = Mathf.Max(0, spikePos - 18);
                int to = Mathf.Min(_count - 1, spikePos + 6);
                sb.Append("  frames around spike (ms): ");
                for (int i = from; i <= to; i++)
                {
                    float v = _ms[(oldest + i) % N];
                    sb.Append(i == spikePos ? $"[{v:0}] " : $"{v:0} ");
                }
                sb.AppendLine();
                sb.AppendLine("================================================================");

                File.AppendAllText(LogPath, sb.ToString());
                LastMarker = $"#{MarkerCount} {reason}: {spikeMs:0.0}ms" + (hasSystems ? $" / {_topT[spike * TOP]?.Name}" : "");
                Plugin.Log?.LogInfo($"[LagCapture] {LastMarker}  ->  {LogPath}");

                // Convenience: if attribution was off, turn it on now so the NEXT spike is attributed to a system.
                if (!hasSystems && !Plugin.LagCaptureEnabled.Value)
                {
                    Plugin.LagCaptureEnabled.Value = true;
                    Plugin.Log?.LogInfo("[LagCapture] per-system capture auto-enabled — reproduce the spike and press the key again to see which system caused it.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("LagCapture.Mark failed: " + e.Message);
            }
        }
    }
}
