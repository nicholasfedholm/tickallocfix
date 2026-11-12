using System;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;

namespace TickAllocFix
{
    public class TickAllocFixConfig
    {
        /// <summary>Allocation-free LeafBlockDecay.CheckDecayThread.shouldDecay (server decay thread).</summary>
        public bool LeafDecay = true;
        /// <summary>Reuse a thread-static string[1] for IsAnimationActive(params string[]) calls in EntityAgent.OnGameTick.</summary>
        public bool AnimParams = true;
        /// <summary>Log call counters every 60 s.</summary>
        public bool LogStats = false;
    }

    public class TickAllocFixMod : ModSystem
    {
        public const string HarmonyId = "nick.tickallocfix";
        static Harmony harmony;
        static int patchedSides;
        internal static TickAllocFixConfig Config;
        internal static ILogger Log;

        public override double ExecuteOrder() => 0.0;

        public override void StartPre(ICoreAPI api)
        {
            if (Config == null)
            {
                try { Config = api.LoadModConfig<TickAllocFixConfig>("TickAllocFix.json"); }
                catch (Exception e) { api.Logger.Warning("[tickallocfix] bad config, using defaults: {0}", e.Message); }
                if (Config == null)
                {
                    Config = new TickAllocFixConfig();
                    try { api.StoreModConfig(Config, "TickAllocFix.json"); } catch { }
                }
            }
            Log = api.Logger;
            LeafDecayFix.Stats = Config.LogStats;

            // Singleplayer: client and server share one process and one Harmony; patch once.
            if (harmony == null)
            {
                harmony = new Harmony(HarmonyId);
                if (Config.LeafDecay) TryApply("LeafDecay", () => LeafDecayFix.Apply(harmony), api);
                if (Config.AnimParams) TryApply("AnimParams", () => AnimParamsFix.Apply(harmony), api);
            }
            patchedSides++;
        }

        static void TryApply(string name, Action apply, ICoreAPI api)
        {
            try { apply(); }
            catch (Exception e) { api.Logger.Warning("[tickallocfix] {0} not applied: {1}", name, e.Message); }
        }

        public override void Start(ICoreAPI api)
        {
            if (Config.LogStats)
            {
                api.Event.RegisterGameTickListener(_ =>
                {
                    api.Logger.Notification("[tickallocfix] {0} leafCalls={1} leafFallbacks={2}", api.Side, Interlocked.Read(ref LeafDecayFix.Calls), Interlocked.Read(ref LeafDecayFix.Fallbacks));
                }, 60000);
            }
        }

        public override void Dispose()
        {
            if (--patchedSides <= 0 && harmony != null)
            {
                harmony.UnpatchAll(HarmonyId);
                harmony = null;
                Config = null;
            }
        }
    }
}
