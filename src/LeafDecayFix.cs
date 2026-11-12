using System;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace TickAllocFix
{
    /// <summary>Reusable per-thread buffers for the leaf-decay breadth-first search.</summary>
    public sealed class DecayScratch
    {
        public const int SetSize = 2048, SetMask = SetSize - 1, QueueSize = 1024;
        // open-addressing set of (x, y, z, dim); generation stamps give an O(1) clear
        public readonly int[] Gen = new int[SetSize], Kx = new int[SetSize], Ky = new int[SetSize], Kz = new int[SetSize], Kd = new int[SetSize];
        public int CurGen, Count;
        public readonly int[] Qx = new int[QueueSize], Qy = new int[QueueSize], Qz = new int[QueueSize];
        public readonly BlockPos Tmp = new BlockPos(0); // reused probe position

        public void Reset()
        {
            if (++CurGen == int.MaxValue) { Array.Clear(Gen, 0, Gen.Length); CurGen = 1; }
            Count = 0;
        }

        /// <summary>Returns false if the key was already present.</summary>
        public bool Add(int x, int y, int z, int d)
        {
            uint h = unchecked((uint)x * 73856093u ^ (uint)y * 19349663u ^ (uint)z * 83492791u ^ (uint)d * 2654435761u);
            h ^= h >> 16; h = unchecked(h * 0x85ebca6bu); h ^= h >> 13;
            h &= SetMask;
            int g = CurGen;
            while (Gen[h] == g)
            {
                if (Kx[h] == x && Ky[h] == y && Kz[h] == z && Kd[h] == d) return false;
                h = (h + 1) & SetMask;
            }
            Gen[h] = g; Kx[h] = x; Ky[h] = y; Kz[h] = z; Kd[h] = d; Count++;
            return true;
        }
    }

    public static class LeafDecayFix
    {
        [ThreadStatic] static DecayScratch S;
        public static long Calls, Fallbacks;
        public static bool Stats;

        public static void Apply(Harmony h)
        {
            var inner = AccessTools.Inner(typeof(LeafBlockDecay), "CheckDecayThread");
            var target = inner == null ? null : AccessTools.DeclaredMethod(inner, "shouldDecay", new[] { typeof(BlockPos) });
            if (target == null) throw new MissingMethodException("LeafBlockDecay+CheckDecayThread.shouldDecay(BlockPos) not found; game changed, leaf decay left unpatched");
            if (AccessTools.DeclaredField(inner, "sapi") == null) throw new MissingFieldException("CheckDecayThread.sapi not found; leaf decay left unpatched");
            h.Patch(target, prefix: new HarmonyMethod(typeof(LeafDecayFix), nameof(Prefix)));
            TickAllocFixMod.Log?.Notification("[tickallocfix] patched LeafBlockDecay.CheckDecayThread.shouldDecay");
        }

        /// <summary>Same algorithm and the same GetBlock call order as vanilla shouldDecay, on reusable buffers. Returns true (run vanilla) only if a size bound would be exceeded.</summary>
        public static bool Prefix(object __instance, BlockPos startPos, ICoreServerAPI ___sapi, ref bool __result)
        {
            if (Stats) Interlocked.Increment(ref Calls);
            var s = S ??= new DecayScratch();
            s.Reset();
            IBlockAccessor acc = ___sapi.World.BlockAccessor;
            Block block = acc.GetBlock(startPos);
            if (!LeafBlockDecay.canDecay(block)) { __result = false; return false; }

            int head = 0, tail = 0;
            s.Qx[tail] = startPos.X; s.Qy[tail] = startPos.InternalY; s.Qz[tail] = startPos.Z; tail++;
            s.Add(startPos.X, startPos.Y, startPos.Z, startPos.dimension);
            Vec3i[] nb = Vec3i.DirectAndIndirectNeighbours;
            BlockPos tmp = s.Tmp;
            while (head < tail)
            {
                if (s.Count > 600) { __result = false; return false; }
                int qx = s.Qx[head], qy = s.Qy[head], qz = s.Qz[head];
                head++;
                for (int i = 0; i < nb.Length; i++)
                {
                    Vec3i d = nb[i];
                    int nx = qx + d.X, ny = qy + d.Y, nz = qz + d.Z;
                    int iy = ny % 32768, dim = ny / 32768; // == new BlockPos(nx, ny, nz) split
                    if (!s.Add(nx, iy, nz, dim)) continue;
                    tmp.X = nx; tmp.Y = iy; tmp.Z = nz; tmp.dimension = dim;
                    block = acc.GetBlock(tmp);
                    if (LeafBlockDecay.preventsDecay(block)) { __result = false; return false; }
                    int dx = nx - startPos.X, dy = iy - startPos.Y, dz = nz - startPos.Z;
                    if (Math.Abs(dx) > 4 || Math.Abs(dy) > 4 || Math.Abs(dz) > 4)
                    {
                        if (block.Id != 0) { __result = false; return false; }
                    }
                    else if (LeafBlockDecay.canDecay(block))
                    {
                        if (tail >= DecayScratch.QueueSize) { if (Stats) Interlocked.Increment(ref Fallbacks); return true; } // impossible by bound; run vanilla
                        s.Qx[tail] = nx; s.Qy[tail] = ny; s.Qz[tail] = nz; tail++;
                    }
                }
            }
            __result = true;
            return false;
        }
    }
}
