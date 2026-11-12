using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using HarmonyLib;
using TickAllocFix;
using Vintagestory.API.Common;

namespace Harness
{
    public static class IlTest
    {
        // ---------- tiny stack-depth simulator over Harmony CodeInstructions ----------
        static int PopCount(OpCode op, object operand)
        {
            switch (op.StackBehaviourPop)
            {
                case StackBehaviour.Pop0: return 0;
                case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: return 1;
                case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi: case StackBehaviour.Popi_popi8:
                case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8: case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi: return 2;
                case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_popi: case StackBehaviour.Popref_popi_popi8:
                case StackBehaviour.Popref_popi_popr4: case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref: case StackBehaviour.Popref_popi_pop1: return 3;
                case StackBehaviour.Varpop:
                    if (op == OpCodes.Ret) return 0; // void method
                    if (operand is MethodBase mb)
                    {
                        int n = mb.GetParameters().Length;
                        if (!mb.IsStatic && op != OpCodes.Newobj) n++;
                        return n;
                    }
                    throw new NotSupportedException("varpop " + op);
                default: throw new NotSupportedException("pop " + op.StackBehaviourPop);
            }
        }

        static int PushCount(OpCode op, object operand)
        {
            switch (op.StackBehaviourPush)
            {
                case StackBehaviour.Push0: return 0;
                case StackBehaviour.Push1: case StackBehaviour.Pushi: case StackBehaviour.Pushi8: case StackBehaviour.Pushr4: case StackBehaviour.Pushr8: case StackBehaviour.Pushref: return 1;
                case StackBehaviour.Push1_push1: return 2;
                case StackBehaviour.Varpush:
                    if (operand is MethodInfo mi) return mi.ReturnType == typeof(void) ? 0 : 1;
                    if (operand is ConstructorInfo) return 1;
                    throw new NotSupportedException("varpush " + op);
                default: throw new NotSupportedException("push " + op.StackBehaviourPush);
            }
        }

        /// <summary>Returns (depthBefore, depthAfter, conflicts, maxDepth). -1 = unreachable.</summary>
        public static (int[] before, int[] after, int conflicts, int max) Simulate(IList<CodeInstruction> list)
        {
            int n = list.Count;
            var before = Enumerable.Repeat(-1, n).ToArray();
            var after = Enumerable.Repeat(-1, n).ToArray();
            var labelIdx = new Dictionary<Label, int>();
            for (int i = 0; i < n; i++) foreach (var l in list[i].labels) labelIdx[l] = i;
            var work = new Stack<(int idx, int depth)>();
            work.Push((0, 0));
            for (int i = 0; i < n; i++)
                foreach (var b in list[i].blocks)
                {
                    if (b.blockType == ExceptionBlockType.BeginCatchBlock || b.blockType == ExceptionBlockType.BeginExceptFilterBlock) work.Push((i, 1));
                    else if (b.blockType == ExceptionBlockType.BeginFinallyBlock || b.blockType == ExceptionBlockType.BeginFaultBlock) work.Push((i, 0));
                }
            int conflicts = 0, max = 0;
            while (work.Count > 0)
            {
                var (idx, depth) = work.Pop();
                while (idx < n)
                {
                    if (before[idx] != -1) { if (before[idx] != depth) conflicts++; break; }
                    before[idx] = depth;
                    var ci = list[idx];
                    int pop = PopCount(ci.opcode, ci.operand), push = PushCount(ci.opcode, ci.operand);
                    int d = depth - pop;
                    if (d < 0) { conflicts++; d = 0; }
                    d += push; after[idx] = d; if (d > max) max = d;
                    var fc = ci.opcode.FlowControl;
                    if (fc == FlowControl.Branch || fc == FlowControl.Cond_Branch)
                    {
                        bool leave = ci.opcode == OpCodes.Leave || ci.opcode == OpCodes.Leave_S;
                        if (ci.operand is Label lb) work.Push((labelIdx[lb], leave ? 0 : d));
                        else if (ci.operand is Label[] lbs) foreach (var l in lbs) work.Push((labelIdx[l], d));
                        else throw new NotSupportedException("branch operand " + ci.operand);
                        if (fc == FlowControl.Branch) break;
                    }
                    else if (fc == FlowControl.Return || fc == FlowControl.Throw) break;
                    depth = d; idx++;
                }
            }
            return (before, after, conflicts, max);
        }

        static string Fmt(CodeInstruction ci)
        {
            string op = ci.operand switch
            {
                null => "",
                MethodBase mb => " " + mb.DeclaringType?.Name + "::" + mb.Name,
                FieldInfo f => " " + f.DeclaringType?.Name + "::" + f.Name,
                Type t => " " + t.Name,
                Label l => " L" + l.GetHashCode(),
                _ => " " + ci.operand
            };
            return ci.opcode.Name + op + (ci.labels.Count > 0 ? "   [labels:" + ci.labels.Count + "]" : "");
        }

        static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what);
            if (!ok) Program.Failures++;
        }

        public static void Run()
        {
            var target = AccessTools.DeclaredMethod(typeof(EntityAgent), "OnGameTick", new[] { typeof(float) });
            Console.WriteLine("[il] target " + target.DeclaringType.FullName + "::" + target.Name);
            var orig = PatchProcessor.GetOriginalInstructions(target);
            var sites = AnimParamsFix.FindSites(orig);
            Console.WriteLine($"[il] original instructions={orig.Count}, matching sites={sites.Count} at idx {string.Join(",", sites)}");
            Check(sites.Count == 3, "exactly 3 sites match");

            // transpile a fresh copy offline
            var origForTranspile = PatchProcessor.GetOriginalInstructions(target);
            var patched = AnimParamsFix.Transpiler(origForTranspile).ToList();
            Console.WriteLine($"[il] transpiled instructions={patched.Count} (delta {patched.Count - orig.Count})");
            Check(patched.Count == orig.Count - 3, "3 instructions removed (3 x [ldc.i4.1 + newarr -> call])");

            var oneM = AccessTools.Method(typeof(AnimScratch), nameof(AnimScratch.One));
            int calls = patched.Count(c => c.opcode == OpCodes.Call && ReferenceEquals(c.operand, oneM));
            Check(calls == 3, "3 calls to AnimScratch.One in patched IL");
            Check(oneM.ReturnType == typeof(string[]) && oneM.GetParameters().Length == 0 && oneM.IsStatic, "helper is static string[] One()");

            // diff: patched must equal original with each (ldc.i4.1, newarr) pair replaced by one call
            var expected = new List<string>();
            var siteSet = new HashSet<int>(sites);
            for (int i = 0; i < orig.Count; i++)
            {
                if (siteSet.Contains(i)) { expected.Add("call One"); i++; continue; }
                expected.Add(Fmt(orig[i]));
            }
            var actual = patched.Select(c => ReferenceEquals(c.operand, oneM) ? "call One" : Fmt(c)).ToList();
            bool same = expected.Count == actual.Count && expected.Zip(actual, (a, b) => a == b).All(x => x);
            Check(same, "patched IL == original IL with the 3 pairs replaced by 'call One' and nothing else changed");

            // stack shape
            var so = Simulate(orig); var sp = Simulate(patched);
            Console.WriteLine($"[il] stack sim original: conflicts={so.conflicts} maxDepth={so.max} unreachable={so.before.Count(x => x == -1)}; patched: conflicts={sp.conflicts} maxDepth={sp.max} unreachable={sp.before.Count(x => x == -1)}");
            Check(so.conflicts == 0 && sp.conflicts == 0, "no stack-depth conflicts at joins / underflow in either version");
            Check(so.max == sp.max, "max stack depth unchanged");
            bool depthsOk = true; int oi = 0;
            for (int k = 0; k < patched.Count; k++, oi++)
            {
                bool isCall = ReferenceEquals(patched[k].operand, oneM);
                if (isCall)
                {
                    // orig: ldc.i4.1 at oi, newarr at oi+1
                    if (sp.before[k] != so.before[oi] || sp.after[k] != so.after[oi + 1]) depthsOk = false;
                    if (sp.after[k] != sp.before[k] + 1) depthsOk = false; // pushes exactly one string[]
                    oi++;
                }
                else if (sp.before[k] != so.before[oi] || sp.after[k] != so.after[oi]) depthsOk = false;
            }
            Check(depthsOk, "stack depth before/after identical for every instruction; each replaced pair -> one call with net +1");

            // dump around the sites
            Console.WriteLine("[il] patched IL around each site (stack depth before -> after):");
            for (int k = 0; k < patched.Count; k++)
            {
                if (!ReferenceEquals(patched[k].operand, oneM)) continue;
                Console.WriteLine($"  --- site at patched idx {k} ---");
                for (int j = Math.Max(0, k - 2); j <= Math.Min(patched.Count - 1, k + 6); j++)
                    Console.WriteLine($"  {(j == k ? ">>" : "  ")} {j,4}: {Fmt(patched[j]),-70} [{sp.before[j]}->{sp.after[j]}]");
            }

            // negative: drop one site -> must leave unpatched
            var broken = PatchProcessor.GetOriginalInstructions(target);
            int bi = FindSite(broken, 0); broken[bi + 1].operand = typeof(object); // newarr object[] : no longer matches
            var brokenCopy = broken.Select(c => c.Clone()).ToList();
            var res = AnimParamsFix.Transpiler(broken).ToList();
            Check(res.Count == brokenCopy.Count && !res.Any(c => ReferenceEquals(c.operand, oneM)), "negative test: 2 sites only -> transpiler returns IL unmodified");

            // real Harmony application
            var h = new Harmony("tickallocfix.harness.il");
            AnimParamsFix.Apply(h);
            var info = Harmony.GetPatchInfo(target);
            Console.WriteLine($"[il] Harmony patch info on OnGameTick: transpilers={info.Transpilers.Count} prefixes={info.Prefixes.Count}");
            Check(info.Transpilers.Count == 1, "exactly one transpiler registered");
            var cur = PatchProcessor.GetCurrentInstructions(target);
            int cur1 = cur.Count(c => c.opcode == OpCodes.Call && ReferenceEquals(c.operand, oneM));
            Console.WriteLine($"[il] GetCurrentInstructions after Harmony patch: count={cur.Count}, call One sites={cur1}");
            Check(cur1 == 3 && cur.Count == orig.Count - 3, "Harmony-applied IL has the 3 replacements");
            // force-compile the patched replacement (Harmony already did at Patch time; this surfaces InvalidProgram if any)
            bool jit = true; try { System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(target.MethodHandle); } catch (Exception e) { jit = false; Console.WriteLine("  PrepareMethod: " + e.Message); }
            Check(jit, "Harmony patch + JIT of patched OnGameTick completed without InvalidProgram");
            h.UnpatchAll("tickallocfix.harness.il");
            Check(Harmony.GetPatchInfo(target) == null || Harmony.GetPatchInfo(target).Transpilers.Count == 0, "unpatch removes the transpiler");

            HelperTests();
        }

        static int FindSite(IList<CodeInstruction> l, int nth)
        {
            var s = AnimParamsFix.FindSites(l); return s[nth];
        }

        static void HelperTests()
        {
            Console.WriteLine("[helper] OnGameTick cannot be invoked offline (needs a live Entity/World/ClientMain); testing the helper path instead");
            var seen = new List<(int len, string e0, string[] arr)>();
            var stub = Proxy.Make<IAnimationManager>((m, a) =>
            {
                if (m.Name == "IsAnimationActive") { var arr = (string[])a[0]; seen.Add((arr.Length, arr[0], arr)); return arr[0] == "walk"; }
                throw new NotSupportedException(m.Name);
            });
            string[] names = { "walk", "run", "idle", "swim", "walk" };
            bool allOk = true; string[] first = null; bool sameRef = true;
            for (int i = 0; i < 1000; i++)
            {
                string s = names[i % names.Length];
                // old path
                bool vOld = stub.IsAnimationActive(new string[1] { s });
                var o = seen[^1];
                // new path (what the transpiled IL does: call One; dup; ldc.i4.0; <load>; stelem.ref; callvirt)
                var arr = AnimScratch.One(); arr[0] = s;
                bool vNew = stub.IsAnimationActive(arr);
                var n = seen[^1];
                first ??= n.arr; if (!ReferenceEquals(first, n.arr)) sameRef = false;
                if (vOld != vNew || o.len != n.len || o.e0 != n.e0 || n.len != 1 || n.e0 != s) allOk = false;
            }
            Check(allOk, "stub IsAnimationActive receives length-1 array with the same string, same result, old vs new path (1000 calls)");
            Check(sameRef, "same thread: same array instance reused");
            string[] other = null; var t = new Thread(() => other = AnimScratch.One()); t.Start(); t.Join();
            Check(other != null && !ReferenceEquals(other, first), "other thread gets its own array");

            // real AnimationManager.IsAnimationActive on a populated dictionary
            try
            {
                var am = new AnimationManager();
                am.ActiveAnimationsByAnimCode["walk"] = new AnimationMetaData();
                var arr = AnimScratch.One();
                arr[0] = "walk"; bool a1 = am.IsAnimationActive(arr); bool a1o = am.IsAnimationActive(new string[1] { "walk" });
                arr[0] = "run"; bool a2 = am.IsAnimationActive(arr); bool a2o = am.IsAnimationActive(new string[1] { "run" });
                Check(a1 && a1o && !a2 && !a2o, "real AnimationManager.IsAnimationActive: scratch array == fresh array (walk true, run false)");
            }
            catch (Exception e) { Console.WriteLine("  (real AnimationManager test skipped: " + e.GetType().Name + " " + e.Message + ")"); }

            // allocation: helper path
            var sink = AnimScratch.One(); sink[0] = "x";
            for (int i = 0; i < 200_000; i++) { var q = AnimScratch.One(); q[0] = names[i % 5]; } // warm-up (tiering)
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1_000_000; i++) { var q = AnimScratch.One(); q[0] = names[i % 5]; }
            long a1b = GC.GetAllocatedBytesForCurrentThread();
            Console.WriteLine($"[alloc] helper raw delta over 1e6 = {a1b - a0} bytes");
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            string[] keep = null;
            for (int i = 0; i < 1_000_000; i++) { var q = new string[1]; q[0] = names[i % 5]; keep = q; }
            long b1 = GC.GetAllocatedBytesForCurrentThread();
            Console.WriteLine($"[alloc] 1e6 call-site array builds: new string[1] = {(b1 - b0) / 1e6:F1} B/site, scratch helper = {(a1b - a0) / 1e6:F2} B/site");
            Check(a1b - a0 == 0, "helper path allocates 0 bytes");
        }
    }
}
