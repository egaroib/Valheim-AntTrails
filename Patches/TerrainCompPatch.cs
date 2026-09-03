using HarmonyLib;

namespace AntTrails.Patches
{
    /// <summary>
    /// Adds our batched paint RPC to every zone's terrain compiler, and treats the
    /// compiler spawning as "this zone just loaded" so tiles that decayed while nobody
    /// was around get caught up.
    ///
    /// Verified against TerrainComp.Awake, which assigns m_nview first, then bails out
    /// early if no heightmap was found -- so the postfix must not assume either is set.
    /// </summary>
    [HarmonyPatch(typeof(TerrainComp), "Awake")]
    internal static class TerrainCompAwakePatch
    {
        private static void Postfix(TerrainComp __instance)
        {
            var nview = __instance.m_nview;
            if (nview == null || !nview.IsValid())
            {
                return;
            }

            nview.Register<ZPackage>(
                TerrainPainter.PaintRpc,
                (sender, pkg) => TerrainPainter.HandlePaintRpc(__instance, sender, pkg));

            if (__instance.m_hmap == null)
            {
                return;
            }

            var pos = __instance.transform.position;
            var zone = ZoneSystem.GetZone(pos);
            TrailEngine.OnZoneLoaded(zone.x, zone.y);
        }
    }
}
