using HarmonyLib;

namespace AntTrails.Patches
{
    /// <summary>
    /// Starts the wear simulation on the peer that owns the world, and registers the
    /// step-reporting RPC on every peer.
    /// </summary>
    [HarmonyPatch(typeof(Game), "Start")]
    internal static class GameStartPatch
    {
        private static void Postfix()
        {
            TrailNetwork.Register();
            StepReporter.Reset();

            if (ZNet.instance == null)
            {
                return;
            }

            if (!ZNet.instance.IsServer())
            {
                AntTrailsPlugin.LogInfo("Client session: reporting steps to the host.");
                return;
            }

            var worldName = ZNet.World != null ? ZNet.World.m_fileName : ZNet.instance.GetWorldName();
            if (string.IsNullOrEmpty(worldName))
            {
                worldName = "unknown";
            }

            TrailEngine.Begin(worldName);
            AntTrailsPlugin.LogInfo($"Host session for '{worldName}': tracking trail wear.");
        }
    }

    /// <summary>Persists wear counters whenever the world itself is saved.</summary>
    [HarmonyPatch(typeof(ZNet), "SaveWorld")]
    internal static class ZNetSaveWorldPatch
    {
        private static void Postfix()
        {
            TrailStore.Save();
        }
    }

    /// <summary>Final save and teardown when leaving a world.</summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
    internal static class ZNetShutdownPatch
    {
        private static void Prefix(bool save)
        {
            if (save)
            {
                TrailStore.Save(force: true);
            }

            TrailEngine.End();
            TrailNetwork.Reset();
            StepReporter.Reset();
        }
    }

    /// <summary>Leaving without saving still has to drop session state.</summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.ShutdownWithoutSave))]
    internal static class ZNetShutdownWithoutSavePatch
    {
        private static void Prefix()
        {
            TrailEngine.End();
            TrailNetwork.Reset();
            StepReporter.Reset();
        }
    }
}
