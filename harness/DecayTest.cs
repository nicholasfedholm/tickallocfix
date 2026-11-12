using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using TickAllocFix;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Harness
{
    /// <summary>DispatchProxy standing in for the huge game interfaces.</summary>
    public class Proxy : DispatchProxy
    {
        public System.Func<MethodInfo, object[], object> Handler;
        protected override object Invoke(MethodInfo targetMethod, object[] args) => Handler(targetMethod, args);
        public static T Make<T>(System.Func<MethodInfo, object[], object> h)
        {
            T t = DispatchProxy.Create<T, Proxy>();
            ((Proxy)(object)t).Handler = h;
            return t;
        }
    }

    public static class FakeWorld
    {
        public static Dictionary<(int, int, int), Block> World = new Dictionary<(int, int, int), Block>();
        public static Block Air;
        // recording / checking
        public static bool Record;
        public static List<int> Trace = new List<int>();
        public static int[] Expect; public static int ExpectIdx; public static bool Mismatch; public static string MismatchWhy;
        public static bool Check;
        public static long GetBlockCalls;

        public static Block GetBlock(BlockPos p)
        {
            GetBlockCalls++;
            int iy = unchecked(p.Y + p.dimension * 32768);
            if (Record) { Trace.Add(p.X); Trace.Add(p.Y); Trace.Add(p.dimension); Trace.Add(p.Z); }
            else if (Check)
            {
                int k = ExpectIdx; ExpectIdx += 4;
                if (Expect == null || k + 3 >= Expect.Length) { if (!Mismatch) MismatchWhy = "extra GetBlock call"; Mismatch = true; }
                else if (Expect[k] != p.X || Expect[k + 1] != p.Y || Expect[k + 2] != p.dimension || Expect[k + 3] != p.Z) { if (!Mismatch) MismatchWhy = "arg differs at call " + (k / 4); Mismatch = true; }
            }
            return World.TryGetValue((p.X, iy, p.Z), out var b) ? b : Air;
        }

        public static ICoreServerAPI MakeApi()
        {
            var acc = Proxy.Make<IBlockAccessor>((m, a) =>
            {
                if (m.Name == "GetBlock" && a.Length == 1 && a[0] is BlockPos bp) return GetBlock(bp);
                throw new NotSupportedException("IBlockAccessor." + m.Name);
            });
            var world = Proxy.Make<IServerWorldAccessor>((m, a) =>
            {
                if (m.Name == "get_BlockAccessor") return acc;
                throw new NotSupportedException("World." + m.Name);
            });
            return Proxy.Make<ICoreServerAPI>((m, a) =>
            {
                if (m.Name == "get_World") return world;
                throw new NotSupportedException("Api." + m.Name);
            });
        }
    }

    public static class DecayTest
    {
        static Block Mk(int id, EnumBlockMaterial mat, string json)
        {
            var b = new Block();
            b.BlockId = id;
            b.BlockMaterial = mat;
            b.Attributes = json == null ? null : new JsonObject(JToken.Parse(json));
            return b;
        }

        static Block Air, Leaf, LeafNoDecay, LeafNullAttr, Log, LeafPrevents, Stone, SoilNull, WoodNoPrev, LeafDecayStr;
        static Block[] Misc;

        static void InitBlocks()
        {
            Air = Mk(0, EnumBlockMaterial.Air, null);
            Leaf = Mk(1, EnumBlockMaterial.Leaves, "{canDecay:true}");
            LeafNoDecay = Mk(2, EnumBlockMaterial.Leaves, "{canDecay:false}");
            LeafNullAttr = Mk(3, EnumBlockMaterial.Leaves, null);
            Log = Mk(4, EnumBlockMaterial.Wood, "{preventsDecay:true}");
            LeafPrevents = Mk(5, EnumBlockMaterial.Leaves, "{canDecay:true,preventsDecay:true}");
            Stone = Mk(6, EnumBlockMaterial.Stone, "{}");
            SoilNull = Mk(7, EnumBlockMaterial.Soil, null);
            WoodNoPrev = Mk(8, EnumBlockMaterial.Wood, "{preventsDecay:false}");
            LeafDecayStr = Mk(9, EnumBlockMaterial.Leaves, "{canDecay:\"true\"}");
            Misc = new[] { Leaf, Leaf, Leaf, LeafNoDecay, LeafNullAttr, Log, LeafPrevents, Stone, SoilNull, WoodNoPrev, LeafDecayStr };
            FakeWorld.Air = Air;
        }

        static readonly int[] YCentres = {
            0, 3, 100, 255, 32760, 32763, 32765, 32766, 32767, 32768, 32769, 32771, 32775, 32776,
            65535, 65536, 65537, 65539, 98303, 98304, 98306, 32768 * 3 + 7,
            -3, -1, -2, -32768 + 2, -32768, -32770, -32766, -65537, 5, 12, 32768 + 100,
            int.MaxValue - 20, int.MaxValue - 5, int.MinValue + 5
        };
        static readonly int[] XZCentres = { 0, 1, -1, 7, -9, 1000, -2000, 123456, -987654, int.MaxValue - 3, int.MinValue + 3, int.MaxValue - 40, int.MinValue + 40 };

        public class Case { public BlockPos Start; }

        // deterministic per-case generation: fills FakeWorld.World, returns start pos
        static BlockPos Gen(int caseIdx, out int mode)
        {
            var rnd = new Random(1234567 + caseIdx * 7919);
            var w = FakeWorld.World; w.Clear();
            mode = caseIdx % 10;
            int cx = XZCentres[rnd.Next(XZCentres.Length)] + (rnd.Next(4) == 0 ? rnd.Next(-5, 6) : 0);
            int cz = XZCentres[rnd.Next(XZCentres.Length)] + (rnd.Next(4) == 0 ? rnd.Next(-5, 6) : 0);
            int cy = YCentres[rnd.Next(YCentres.Length)];
            void Put(int dx, int dy, int dz, Block b) { w[(unchecked(cx + dx), unchecked(cy + dy), unchecked(cz + dz))] = b; }
            switch (mode)
            {
                case 0: // small isolated blob
                case 1:
                    { int n = rnd.Next(1, 45); Put(0, 0, 0, Leaf); for (int i = 0; i < n; i++) Put(rnd.Next(-3, 4), rnd.Next(-3, 4), rnd.Next(-3, 4), rnd.Next(8) == 0 ? LeafNoDecay : Leaf);
                      if (mode == 1 && rnd.Next(2) == 0) Put(rnd.Next(-6, 7), rnd.Next(-6, 7), rnd.Next(-6, 7), rnd.Next(2) == 0 ? Stone : Leaf); break; }
                case 2: // blob + log somewhere near
                    { int n = rnd.Next(5, 60); Put(0, 0, 0, Leaf); for (int i = 0; i < n; i++) Put(rnd.Next(-4, 5), rnd.Next(-4, 5), rnd.Next(-4, 5), Leaf);
                      Put(rnd.Next(-6, 7), rnd.Next(-6, 7), rnd.Next(-6, 7), rnd.Next(5) == 0 ? LeafPrevents : Log); break; }
                case 3: // random noise box
                    { double p = 0.02 + rnd.NextDouble() * 0.5; for (int x = -6; x <= 6; x++) for (int y = -6; y <= 6; y++) for (int z = -6; z <= 6; z++) if (rnd.NextDouble() < p) Put(x, y, z, Misc[rnd.Next(Misc.Length)]); Put(0, 0, 0, Leaf); break; }
                case 4: // big canopy with trunk
                    { int rx = rnd.Next(3, 10), ry = rnd.Next(2, 8), rz = rnd.Next(3, 10); double hole = rnd.NextDouble() * 0.3;
                      for (int x = -rx; x <= rx; x++) for (int y = -ry; y <= ry; y++) for (int z = -rz; z <= rz; z++)
                          if ((double)x * x / (rx * rx) + (double)y * y / (ry * ry) + (double)z * z / (rz * rz) <= 1.0 && rnd.NextDouble() >= hole) Put(x, y, z, Leaf);
                      if (rnd.Next(3) != 0) for (int y = -ry - 6; y < -ry + 1; y++) Put(0, y, 0, Log);
                      Put(0, 0, 0, Leaf); break; }
                case 5: // start not a leaf, leafy neighbourhood
                    { int n = rnd.Next(1, 30); for (int i = 0; i < n; i++) Put(rnd.Next(-3, 4), rnd.Next(-3, 4), rnd.Next(-3, 4), Leaf); Put(0, 0, 0, Misc[rnd.Next(3, Misc.Length)]); if (rnd.Next(4) == 0) Put(0, 0, 0, Air); break; }
                case 6: // dense cube (forces the 600 bound or outside-block exit)
                    { int r = rnd.Next(2, 6); for (int x = -r; x <= r; x++) for (int y = -r; y <= r; y++) for (int z = -r; z <= r; z++) Put(x, y, z, Leaf); break; }
                case 7: // thin plane / line of leaves
                    { int r = rnd.Next(3, 14); int axis = rnd.Next(3); for (int a = -r; a <= r; a++) for (int b = -r; b <= r; b++) { if (rnd.Next(10) == 0) continue; if (axis == 0) Put(a, 0, b, Leaf); else if (axis == 1) Put(0, a, b, Leaf); else Put(a, b, 0, Leaf); } Put(0, 0, 0, Leaf); break; }
                case 8: // leaves of mixed kinds, small, boundary prone
                    { int n = rnd.Next(2, 80); for (int i = 0; i < n; i++) Put(rnd.Next(-5, 6), rnd.Next(-5, 6), rnd.Next(-5, 6), rnd.Next(4) == 0 ? Misc[rnd.Next(Misc.Length)] : Leaf); Put(0, 0, 0, Leaf); break; }
                default: // empty world, start = leaf alone or air
                    { if (rnd.Next(2) == 0) Put(0, 0, 0, Leaf); break; }
            }
            // start position: canonical 3-int ctor, or noncanonical (Y, dimension) split of the same raw Y
            if (rnd.Next(4) == 0)
            {
                int d = rnd.Next(-2, 4);
                return new BlockPos(cx, unchecked(cy - d * 32768), cz, d);
            }
            return new BlockPos(cx, cy, cz);
        }

        public static void Run(int cases, int allocCases)
        {
            InitBlocks();
            var api = FakeWorld.MakeApi();
            var inner = AccessTools.Inner(typeof(LeafBlockDecay), "CheckDecayThread");
            var ctor = inner.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)[0];
            object inst = ctor.Invoke(new object[] { api });
            var mi = AccessTools.DeclaredMethod(inner, "shouldDecay", new[] { typeof(BlockPos) });
            var vanilla = (System.Func<BlockPos, bool>)Delegate.CreateDelegate(typeof(System.Func<BlockPos, bool>), inst, mi);

            // ---- phase 1: vanilla results + traces ----
            var vRes = new int[cases]; // 0 false, 1 true, 2 exception
            var vTrace = new int[cases][]; var vEx = new string[cases];
            var modeCount = new int[10]; var modeTrue = new int[10];
            int bound = 0, trueN = 0, falseN = 0, exN = 0, maxCalls = 0;
            for (int i = 0; i < cases; i++)
            {
                var start = Gen(i, out int mode);
                FakeWorld.Record = true; FakeWorld.Check = false; FakeWorld.Trace.Clear();
                int r;
                try { r = vanilla(start) ? 1 : 0; } catch (Exception e) { r = 2; vEx[i] = e.GetType().FullName; }
                FakeWorld.Record = false;
                vRes[i] = r; vTrace[i] = FakeWorld.Trace.ToArray();
                int calls = vTrace[i].Length / 4; if (calls > maxCalls) maxCalls = calls;
                if (calls >= 600) bound++;
                modeCount[mode]++; if (r == 1) { trueN++; modeTrue[mode]++; } else if (r == 0) falseN++; else exN++;
            }
            Console.WriteLine($"[leaf] vanilla phase: cases={cases} true={trueN} false={falseN} exceptions={exN} bound-sized(>=600 probes)={bound} maxProbes={maxCalls}");
            Console.WriteLine("[leaf] per-mode cases/true: " + FormatModes(modeCount, modeTrue));

            // ---- phase 2: direct Prefix vs vanilla ----
            long mism = 0; int fallbacks = 0;
            string firstWhy = null;
            for (int i = 0; i < cases; i++)
            {
                var start = Gen(i, out _);
                FakeWorld.Record = false; FakeWorld.Check = true; FakeWorld.Mismatch = false; FakeWorld.Expect = vTrace[i]; FakeWorld.ExpectIdx = 0;
                bool res = false; int r; bool run; string exT = null;
                try { run = LeafDecayFix.Prefix(inst, start, api, ref res); r = run ? -1 : (res ? 1 : 0); } catch (Exception e) { r = 2; run = false; exT = e.GetType().FullName; }
                FakeWorld.Check = false;
                if (r == -1) fallbacks++;
                bool bad = FakeWorld.Mismatch || r != vRes[i] || exT != vEx[i] || (r != 2 && FakeWorld.ExpectIdx != vTrace[i].Length);
                if (bad) { mism++; firstWhy ??= $"case {i}: res vanilla={vRes[i]} fix={r} {FakeWorld.MismatchWhy} idx={FakeWorld.ExpectIdx}/{vTrace[i].Length}"; }
            }
            Console.WriteLine($"[leaf] direct Prefix vs vanilla: cases={cases} mismatches={mism} fallbacks-to-vanilla={fallbacks}" + (firstWhy != null ? " first=" + firstWhy : ""));
            if (mism > 0) Program.Failures++;

            // ---- allocation measurement (before Harmony patching), counting only (no recording) ----
            FakeWorld.Record = false; FakeWorld.Check = false;
            // baselines for the fake accessor's own overhead
            var starts = new BlockPos[allocCases];
            // warm-up
            for (int i = 0; i < 300; i++) { var s = Gen(i, out _); try { vanilla(s); } catch { } bool rr = false; try { LeafDecayFix.Prefix(inst, s, api, ref rr); } catch { } }
            double aOverhead, bOverhead;
            {
                var s0 = Gen(0, out _); var bp = new BlockPos(0);
                var accRef = api.World.BlockAccessor;
                long t0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100000; i++) { var a = api.World.BlockAccessor; }
                long t1 = GC.GetAllocatedBytesForCurrentThread();
                aOverhead = (t1 - t0) / 100000.0;
                for (int i = 0; i < 100000; i++) { accRef.GetBlock(bp); }
                long t2 = GC.GetAllocatedBytesForCurrentThread();
                bOverhead = (t2 - t1) / 100000.0;
            }
            long vAlloc = 0, fAlloc = 0, vCalls = 0, fCalls = 0; int nV = 0, nF = 0, nP = 0;
            for (int pass = 0; pass < 2; pass++) { if (pass == 1) { vAlloc = fAlloc = vCalls = fCalls = 0; nV = nF = 0; Console.WriteLine("[alloc] (pass 0 = warm-up/tiering, pass 1 reported)"); }
            for (int i = 0; i < allocCases; i++)
            {
                var s = Gen(i, out _);
                long c0 = FakeWorld.GetBlockCalls, m0 = GC.GetAllocatedBytesForCurrentThread();
                bool threw = false; try { vanilla(s); } catch { threw = true; }
                long m1 = GC.GetAllocatedBytesForCurrentThread();
                if (!threw) { vAlloc += m1 - m0; vCalls += FakeWorld.GetBlockCalls - c0; nV++; }
            }
            for (int i = 0; i < allocCases; i++)
            {
                var s = Gen(i, out _);
                bool res = false;
                long c0 = FakeWorld.GetBlockCalls, m0 = GC.GetAllocatedBytesForCurrentThread();
                bool threw = false; try { LeafDecayFix.Prefix(inst, s, api, ref res); } catch { threw = true; }
                long m1 = GC.GetAllocatedBytesForCurrentThread();
                if (!threw) { fAlloc += m1 - m0; fCalls += FakeWorld.GetBlockCalls - c0; nF++; }
            }
            }
            double vNet = (vAlloc - nV * aOverhead - vCalls * bOverhead) / nV;
            double fNet = (fAlloc - nF * aOverhead - fCalls * bOverhead) / nF;
            Console.WriteLine($"[alloc] proxy overhead: {aOverhead:F1} B per sapi.World.BlockAccessor chain, {bOverhead:F1} B per GetBlock call (subtracted)");
            Console.WriteLine($"[alloc] {nV} calls (non-throwing of {allocCases}): vanilla gross={vAlloc / (double)nV:F1} B/call, net of fake-accessor overhead={vNet:F1} B/call ({vCalls / (double)nV:F1} GetBlock/call)");
            Console.WriteLine($"[alloc] {nF} calls (non-throwing of {allocCases}): fix     gross={fAlloc / (double)nF:F1} B/call, net of fake-accessor overhead={fNet:F1} B/call ({fCalls / (double)nF:F1} GetBlock/call); residual total={fAlloc - nF * aOverhead - fCalls * bOverhead:F0} B");
            if (Math.Abs(fNet) > 0.5) Program.Failures++;

            // ---- phase 3: real Harmony patch applied to the real method, run through the real method ----
            var h = new Harmony("tickallocfix.harness");
            LeafDecayFix.Apply(h);
            var info = Harmony.GetPatchInfo(mi);
            Console.WriteLine($"[leaf] Harmony patch info on shouldDecay: prefixes={info.Prefixes.Count} postfixes={info.Postfixes.Count} transpilers={info.Transpilers.Count}");
            var patched = (System.Func<BlockPos, bool>)Delegate.CreateDelegate(typeof(System.Func<BlockPos, bool>), inst, mi);
            long mism2 = 0; string why2 = null;
            for (int i = 0; i < cases; i++)
            {
                var start = Gen(i, out _);
                FakeWorld.Record = false; FakeWorld.Check = true; FakeWorld.Mismatch = false; FakeWorld.Expect = vTrace[i]; FakeWorld.ExpectIdx = 0;
                int r; string exT = null; try { r = patched(start) ? 1 : 0; } catch (Exception e) { r = 2; exT = e.GetType().FullName; }
                FakeWorld.Check = false;
                bool bad = FakeWorld.Mismatch || r != vRes[i] || exT != vEx[i] || (r != 2 && FakeWorld.ExpectIdx != vTrace[i].Length);
                if (bad) { mism2++; why2 ??= $"case {i}: vanilla={vRes[i]} patched={r} {FakeWorld.MismatchWhy}"; }
            }
            Console.WriteLine($"[leaf] Harmony-patched real method vs stored vanilla: cases={cases} mismatches={mism2}" + (why2 != null ? " first=" + why2 : ""));
            if (mism2 > 0) Program.Failures++;
            FakeWorld.Check = false;
            long pAlloc = 0, pCalls = 0;
            for (int pass = 0; pass < 2; pass++) { pAlloc = pCalls = 0; nP = 0;
            for (int i = 0; i < allocCases; i++)
            {
                var s = Gen(i, out _);
                long c0 = FakeWorld.GetBlockCalls, m0 = GC.GetAllocatedBytesForCurrentThread();
                bool threw = false; try { patched(s); } catch { threw = true; }
                long m1 = GC.GetAllocatedBytesForCurrentThread();
                if (!threw) { pAlloc += m1 - m0; pCalls += FakeWorld.GetBlockCalls - c0; nP++; }
            } }
            double pNet = (pAlloc - nP * aOverhead - pCalls * bOverhead) / nP;
            Console.WriteLine($"[alloc] Harmony-patched real method: gross={pAlloc / (double)nP:F1} B/call, net={pNet:F1} B/call");
            h.UnpatchAll("tickallocfix.harness");
        }

        static string FormatModes(int[] c, int[] t)
        {
            var parts = new List<string>();
            for (int i = 0; i < c.Length; i++) parts.Add($"m{i}={c[i]}/{t[i]}");
            return string.Join(" ", parts);
        }
    }
}
