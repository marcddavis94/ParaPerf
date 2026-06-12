using HarmonyLib;

namespace ParaPerf
{
    // SAFE perf fix #2 — kill the eager log-string GC churn on the per-frame evaluator / brain-logic hot paths.
    //
    // Across the sim, requirement evaluators and brain logic build descriptive log strings as EAGER METHOD
    // ARGUMENTS that are then thrown away. The pattern (ContextEvaluationManager, ~40 ContextEvaluator leaves,
    // BrainLogicManager, Logger<T>) is:
    //     data.LogStringBuilder?.AppendLine(StaticLoggerUtils.GetUrledMsg($"...", line, file))
    // The `?.` only null-guards AppendLine; the $"..." interpolation AND the GetUrledMsg wrapper are evaluated
    // every call as arguments, even though LogStringBuilder is null in normal play. Live profiling (ParaScope
    // perftop) showed UpdateCharacterMemories alone churning ~205 KB/frame of this throwaway garbage — constant
    // gen-0 GC -> the stutter.
    //
    // GetUrledMsg/Error/Good each wrap their message in
    //     <a href="C:\Users\poik0\Documents\paralives\paralives\Assets\Scripts\X.cs" line="N"><color=#......>{msg}</color></a>
    // i.e. a ~120+ char string DOMINATED by a hardcoded dev file path, freshly allocated at every eager site,
    // every frame. We Prefix the three wrappers to return the message UNWRAPPED — one patch, every site at once.
    //
    // Why this is safe: the wrapper exists only so a developer's in-editor log console can render a clickable
    // source link. Shipped players have no such console, and the strings are discarded (LogStringBuilder null).
    // When the game's dev logging IS toggled on, the log still receives the full message text — only the
    // <a href>/<color> markup is dropped. No gameplay path reads these strings.
    [HarmonyPatch(typeof(StaticLoggerUtils))]
    internal static class LogStringGCPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(StaticLoggerUtils.GetUrledMsg))]
        private static bool GetUrledMsg(string msg, ref string __result)
        {
            if (!Plugin.ReduceLogStringGC.Value) return true;   // disabled -> vanilla wrapper
            __result = msg ?? "";                               // skip the <a href><color> wrapper alloc
            return false;                                       // skip the original
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(StaticLoggerUtils.GetUrledMsgError))]
        private static bool GetUrledMsgError(string msg, ref string __result)
        {
            if (!Plugin.ReduceLogStringGC.Value) return true;
            __result = msg ?? "";
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(StaticLoggerUtils.GetUrledMsgGood))]
        private static bool GetUrledMsgGood(string msg, ref string __result)
        {
            if (!Plugin.ReduceLogStringGC.Value) return true;
            __result = msg ?? "";
            return false;
        }
    }
}
