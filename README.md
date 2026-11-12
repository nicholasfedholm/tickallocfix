# Tick Alloc Fix (tickallocfix) 1.0.0

Vintage Story code mod (Universal, not required on client or server). Removes per-tick garbage with behavior identical to vanilla.
Config: ModConfig/TickAllocFix.json (LeafDecay, AnimParams, LogStats).

1. LeafDecay: Harmony prefix on LeafBlockDecay+CheckDecayThread.shouldDecay, same BFS and same GetBlock call order on thread-static buffers (0 B/call instead of ~24 KB/call).
2. AnimParams: transpiler on EntityAgent.OnGameTick, 3 x (ldc.i4.1; newarr string) feeding IAnimationManager.IsAnimationActive replaced by a call returning a thread-static string[1]. Applied only if exactly 3 sites match.

Build: `dotnet build src -c Release` (game dir from -p:VSDir=... or %LOCALAPPDATA%\Programs\Optimum). Offline tests: `dotnet run --project harness -c Release`.
