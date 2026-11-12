using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;

namespace TickAllocFix
{
    public static class AnimScratch
    {
        [ThreadStatic] static string[] one;
        /// <summary>Thread-static string[1]; the caller overwrites element 0 and passes it straight to IsAnimationActive.</summary>
        public static string[] One() => one ??= new string[1];
    }

    public static class AnimParamsFix
    {
        public const int ExpectedSites = 3;

        public static void Apply(Harmony h)
        {
            var target = AccessTools.DeclaredMethod(typeof(EntityAgent), "OnGameTick", new[] { typeof(float) });
            if (target == null) throw new MissingMethodException("EntityAgent.OnGameTick(float) not found; left unpatched");
            // Dry run first: only patch if the IL matches exactly 3 sites.
            var original = PatchProcessor.GetOriginalInstructions(target);
            int found = CountSites(original);
            if (found != ExpectedSites)
                throw new InvalidOperationException("EntityAgent.OnGameTick IL changed (expected " + ExpectedSites + " IsAnimationActive array sites, found " + found + "); left unpatched");
            h.Patch(target, transpiler: new HarmonyMethod(typeof(AnimParamsFix), nameof(Transpiler)));
            TickAllocFixMod.Log?.Notification("[tickallocfix] patched EntityAgent.OnGameTick ({0} IsAnimationActive sites)", ExpectedSites);
        }

        static MethodInfo IsActive => AccessTools.Method(typeof(IAnimationManager), nameof(IAnimationManager.IsAnimationActive), new[] { typeof(string[]) });

        static bool IsSimpleLoad(CodeInstruction ci)
        {
            var op = ci.opcode;
            return op == OpCodes.Ldloc || op == OpCodes.Ldloc_S || op == OpCodes.Ldloc_0 || op == OpCodes.Ldloc_1 || op == OpCodes.Ldloc_2 || op == OpCodes.Ldloc_3
                || op == OpCodes.Ldarg || op == OpCodes.Ldarg_S || op == OpCodes.Ldarg_0 || op == OpCodes.Ldarg_1 || op == OpCodes.Ldarg_2 || op == OpCodes.Ldarg_3
                || op == OpCodes.Ldfld;
        }

        /// <summary>
        /// Site shape (strict): ldc.i4.1; newarr string; dup; ldc.i4.0; {ldloc/ldarg/ldfld x1..4}; stelem.ref; callvirt IAnimationManager.IsAnimationActive(string[]).
        /// The array is stored into once and consumed by the very next call: it never escapes. Returns the index of the ldc.i4.1 for each site.
        /// </summary>
        public static List<int> FindSites(IList<CodeInstruction> list)
        {
            var sites = new List<int>();
            var isActive = IsActive;
            if (isActive == null) return sites;
            for (int i = 0; i + 6 < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Ldc_I4_1) continue;
                if (list[i + 1].opcode != OpCodes.Newarr || !(list[i + 1].operand is Type t) || t != typeof(string)) continue;
                if (list[i + 2].opcode != OpCodes.Dup || list[i + 3].opcode != OpCodes.Ldc_I4_0) continue;
                int j = i + 4;
                while (j < list.Count && j <= i + 8 && IsSimpleLoad(list[j])) j++;
                if (j == i + 4 || j + 1 >= list.Count) continue;
                if (list[j].opcode != OpCodes.Stelem_Ref) continue;
                if (!list[j + 1].Calls(isActive)) continue;
                sites.Add(i);
            }
            return sites;
        }

        public static int CountSites(IList<CodeInstruction> list) => FindSites(list).Count;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            var list = new List<CodeInstruction>(code);
            var sites = FindSites(list);
            if (sites.Count != ExpectedSites)
            {
                TickAllocFixMod.Log?.Warning("[tickallocfix] EntityAgent.OnGameTick: expected {0} sites, found {1}; leaving unpatched", ExpectedSites, sites.Count);
                return list;
            }
            var one = AccessTools.Method(typeof(AnimScratch), nameof(AnimScratch.One));
            // back to front so indexes stay valid
            for (int k = sites.Count - 1; k >= 0; k--)
            {
                int i = sites[k];
                var call = new CodeInstruction(OpCodes.Call, one);
                call.labels.AddRange(list[i].labels);   // keep jump targets / exception blocks that pointed at ldc.i4.1
                call.blocks.AddRange(list[i].blocks);
                list[i + 1] = call;                     // replaces newarr
                list.RemoveAt(i);                       // drops ldc.i4.1
            }
            return list;
        }
    }
}
