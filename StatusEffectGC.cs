using HarmonyLib;
using Setting;

namespace ParaPerf
{
    // SAFE perf fix #5 — no-alloc status-effect value reads.
    //
    // StatusEffectManager.GetStatusEffectValueForEffectType (StatusEffectManager.cs:114) allocates a
    // List<StatusEffectReturnValues> on EVERY call (via GetStatusEffectLogicsAndDurationsByEffectType, whose
    // `new List` runs before its own null-guard), only to fold each entry's .Value into a running sum and throw
    // the list away. It's hit every frame across the whole roster by need reads (UpdateCharacterWriteNeeds ->
    // NeedManager.GetNeedValue) and skill reads (UpdateCharacterLearningSkills) — a chunk of the live GC churn
    // (~23K + ~7K/frame, and GC pauses jank BOTH viewports in splitscreen).
    //
    // Fix: reimplement the value-only path inline with NO list — walk the character's status-effect saves and
    // accumulate the value directly through the game's own PUBLIC GetAffectedData / GetValueForType /
    // AddStatusValue. Faithful to the original (same multiplier seed, same affected-data filter, same fold order),
    // it only drops the throwaway List. It deliberately does NOT touch GetStatusEffectLogicsAndDurationsByEffectType
    // (the list-returning method SkillManager.GetSkillLearningSpeed holds two of at once), so the audit's
    // re-entrancy / two-lists-alive pooling hazard is avoided. Stateless -> re-entrancy-safe.
    [HarmonyPatch(typeof(StatusEffectManager), nameof(StatusEffectManager.GetStatusEffectValueForEffectType))]
    internal static class StatusEffectValueNoAllocPatch
    {
        private static bool Prefix(StatusEffectEffectTypes effectType, ulong dataToCheck, AssetCharacter character,
                                   StatusEffectManager __instance, ref System.ValueTuple<bool, float> __result)
        {
            try
            {
                if (!Plugin.On(Plugin.TrimStatusEffectGC)) return true;   // disabled / master off -> vanilla

                float num = (effectType == StatusEffectEffectTypes.NeedReplenishSpeedMultiplier
                          || effectType == StatusEffectEffectTypes.FlexibleActionSpeedMultiplier) ? 1f : 0f;
                bool found = false;

                var byType = __instance.StatusEffectsByEffectTypes;
                if (byType != null && character != null && character.Data != null && byType.ContainsKey(effectType))
                {
                    var typeDict = byType[effectType];
                    var saves = character.Data.StatusEffectSaveData;
                    for (int i = 0; i < saves.Count; i++)
                    {
                        AssetCharacterStatusEffectSaveData sd = saves[i];
                        if (sd == null || !sd.Active || !typeDict.ContainsKey(sd.StatusEffectGUID)) continue;
                        StatusEffect se = typeDict[sd.StatusEffectGUID];
                        if (se == null || se.Effects == null || se.Effects.Length == 0
                            || character.Data.MaskedFromStatusEffects.ContainsKey(se.GUID)) continue;
                        StatusEffectEffect[] effects = se.Effects;
                        for (int e = 0; e < effects.Length; e++)
                        {
                            StatusEffectEffect eff = effects[e];
                            if (eff.EffectType != effectType) continue;
                            ulong affectedData = __instance.GetAffectedData(sd, eff);
                            if (affectedData == 0UL || affectedData == dataToCheck)
                            {
                                float val = __instance.GetValueForType(sd, eff, character, dataToCheck);
                                num = __instance.AddStatusValue(num, effectType, found, val);
                                found = true;
                            }
                        }
                    }
                }

                __result = new System.ValueTuple<bool, float>(found, num);
                return false;   // skip the original (allocating) implementation
            }
            catch
            {
                return true;     // anything unexpected -> run the original
            }
        }
    }
}
