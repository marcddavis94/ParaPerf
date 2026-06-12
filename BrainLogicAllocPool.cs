using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Setting;

namespace ParaPerf
{
    // SAFE perf fix #3 — pool the per-frame scratch-list allocations in BrainLogicManager.Process /
    // GetProcessedOutcomes.
    //
    // Live profiling (perftop) pinned ~205-226 KB/frame of GC churn to UpdateCharacterMemories, whose
    // LateUpdate runs brain logic for every visual-loaded Para every frame. Each BrainLogicManager.Process
    // call allocates throwaway heap garbage: `new List<BrainLogicLine>()` + AddRange (a pointless copy of an
    // array it only reads), and GetProcessedOutcomes' `new List<Outcome>()` + recursive `lines.GetRange(...)`
    // (a fresh sublist per IF/ELSE/PICK branch). All of it is dead the moment Process returns.
    //
    // APPROACH — algorithm-preserving. We do NOT reimplement the recursive interpreter (that would risk silent
    // sim corruption that perftop can't detect). A Harmony TRANSPILER swaps ONLY the allocation instructions
    // (`new List<...>()` -> a pooled rent; `List<BrainLogicLine>.GetRange` -> a pooled copy) and leaves 100% of
    // the control flow and index math untouched. A Prefix/Finalizer pair scopes the pool around each Process
    // call: the Finalizer runs even if the body throws (exception-safe), and the high-water mark is carried in
    // __state (no allocation, correct under reentrancy/nesting).
    //
    // SAFETY:
    //  - Lists are pooled only WHILE inside a Process scope (depth-gated). GetProcessedOutcomes has external
    //    callers (TogetherManager) that run outside Process — those get plain `new` lists, vanilla behaviour,
    //    no pool leak.
    //  - The rented lists never escape Process: the List<Outcome> result is iterated and discarded within
    //    Process; the Outcome objects inside are persistent game assets (never pooled). Verified by reading the
    //    source — nothing retains the lists.
    //  - On the master toggle off, rents fall back to `new` => byte-for-byte vanilla.
    internal static class BrainLogicPool
    {
        private const long Sentinel = long.MinValue;   // "scope not entered" (toggle off) — can't collide with a packed mark

        private static int _depth;                                                   // >0 while inside a Process scope
        private static readonly List<List<BrainLogicLine>> _linePool = new List<List<BrainLogicLine>>();
        private static int _lineHigh;
        private static readonly List<List<Outcome>> _outcomePool = new List<List<Outcome>>();
        private static int _outcomeHigh;

        // A single read-only empty MemoryData shared in place of BrainLogicManager.Process's per-call
        // `new MemoryData()`. Safe because that instance is never written: in Process it is only read into
        // ContextData/OutcomeData, and xref confirms only two OutcomeProcessors ever read OutcomeData.Memory
        // (AddStatusEffectProcessor, OfferSkillWantForJobPerformanceProcessor) — both read fields synchronously,
        // neither stores nor mutates it. When `memory != null` the game overwrites it with memory.Data anyway,
        // so this just removes the throwaway allocation.
        private static readonly MemoryData _emptyMemory = new MemoryData();

        // --- scope management (Prefix/Finalizer on Process) ---------------------------------------
        internal static long Enter()
        {
            if (!Plugin.On(Plugin.PoolBrainLogicAllocs)) return Sentinel;
            _depth++;
            return ((long)_lineHigh << 32) | (uint)_outcomeHigh;   // pack both high-water marks, no alloc
        }

        internal static void Exit(long state)
        {
            if (state == Sentinel) return;
            if (_depth > 0) _depth--;
            _lineHigh = (int)((ulong)state >> 32);                 // free every list rented during this scope
            _outcomeHigh = (int)(state & 0xFFFFFFFFL);
        }

        // --- pooled allocators (transpiler redirects the game's `new`/GetRange here) ---------------
        internal static List<BrainLogicLine> RentLines()
        {
            if (_depth <= 0) return new List<BrainLogicLine>();    // outside scope / disabled -> vanilla
            return RentLineList();
        }

        internal static List<Outcome> RentOutcomes()
        {
            if (_depth <= 0) return new List<Outcome>();
            List<Outcome> l;
            if (_outcomeHigh < _outcomePool.Count) { l = _outcomePool[_outcomeHigh]; l.Clear(); }
            else { l = new List<Outcome>(); _outcomePool.Add(l); }
            _outcomeHigh++;
            return l;
        }

        // Replaces List<BrainLogicLine>.GetRange(index, count) — same semantics (a fresh 0-based copy), pooled.
        internal static List<BrainLogicLine> RentRange(List<BrainLogicLine> src, int index, int count)
        {
            if (_depth <= 0) return src.GetRange(index, count);    // vanilla
            List<BrainLogicLine> l = RentLineList();
            for (int i = 0; i < count; i++) l.Add(src[index + i]);
            return l;
        }

        // Replaces `new MemoryData()` in Process — returns the shared read-only empty (vanilla `new` if disabled).
        internal static MemoryData EmptyMemory()
            => Plugin.On(Plugin.PoolBrainLogicAllocs) ? _emptyMemory : new MemoryData();

        private static List<BrainLogicLine> RentLineList()
        {
            List<BrainLogicLine> l;
            if (_lineHigh < _linePool.Count) { l = _linePool[_lineHigh]; l.Clear(); }
            else { l = new List<BrainLogicLine>(); _linePool.Add(l); }
            _lineHigh++;
            return l;
        }

        // --- the transpiler: swap only the allocation instructions, nothing else -------------------
        internal static IEnumerable<CodeInstruction> PoolTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo rentLines = AccessTools.Method(typeof(BrainLogicPool), nameof(RentLines));
            MethodInfo rentOutcomes = AccessTools.Method(typeof(BrainLogicPool), nameof(RentOutcomes));
            MethodInfo rentRange = AccessTools.Method(typeof(BrainLogicPool), nameof(RentRange));
            MethodInfo emptyMemory = AccessTools.Method(typeof(BrainLogicPool), nameof(EmptyMemory));

            foreach (CodeInstruction ins in instructions)
            {
                // `new List<BrainLogicLine>()` / `new List<Outcome>()` / `new MemoryData()` (parameterless ctor)
                if (ins.opcode == OpCodes.Newobj && ins.operand is ConstructorInfo ci && ci.GetParameters().Length == 0)
                {
                    if (IsList(ci.DeclaringType, out System.Type arg))
                    {
                        if (arg == typeof(BrainLogicLine)) { ins.opcode = OpCodes.Call; ins.operand = rentLines; }
                        else if (arg == typeof(Outcome)) { ins.opcode = OpCodes.Call; ins.operand = rentOutcomes; }
                    }
                    else if (ci.DeclaringType == typeof(MemoryData))
                    {
                        ins.opcode = OpCodes.Call; ins.operand = emptyMemory;   // share one read-only empty
                    }
                }
                // `someList.GetRange(int, int)` where someList is List<BrainLogicLine>
                else if ((ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call) && ins.operand is MethodInfo mi
                    && mi.Name == "GetRange" && IsList(mi.DeclaringType, out System.Type rarg) && rarg == typeof(BrainLogicLine))
                {
                    ins.opcode = OpCodes.Call; ins.operand = rentRange;
                }
                yield return ins;   // mutated in place -> labels / exception blocks preserved
            }
        }

        private static bool IsList(System.Type t, out System.Type elementType)
        {
            elementType = null;
            if (t == null || !t.IsGenericType || t.GetGenericTypeDefinition() != typeof(List<>)) return false;
            elementType = t.GetGenericArguments()[0];
            return true;
        }
    }

    [HarmonyPatch(typeof(BrainLogicManager), nameof(BrainLogicManager.Process))]
    internal static class BrainLogicProcessScopePatch
    {
        private static void Prefix(out long __state) { __state = BrainLogicPool.Enter(); }
        private static void Finalizer(long __state) { BrainLogicPool.Exit(__state); }   // runs even if Process throws
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => BrainLogicPool.PoolTranspiler(instructions);
    }

    [HarmonyPatch(typeof(BrainLogicManager), nameof(BrainLogicManager.GetProcessedOutcomes))]
    internal static class BrainLogicGetProcessedOutcomesPoolPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => BrainLogicPool.PoolTranspiler(instructions);
    }
}
