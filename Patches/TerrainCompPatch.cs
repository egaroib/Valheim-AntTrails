using HarmonyLib;

namespace AntTrails.Patches
{
    /// <summary>
    /// Treats a terrain compiler spawning as "this zone just loaded", so tiles that decayed
    /// unwatched get caught up.
    ///
    /// This only ever fires usefully on a host-and-play session. A dedicated server builds
    /// live zones solely around ZNet.GetReferencePosition(), which stays at the origin with
    /// no local player, so no TerrainComp ever awakes near a player there -- TrailEngine's
    /// decay sweep covers that case by walking zones near connected peers instead.
    ///
    /// Verified against TerrainComp.Awake, which assigns m_nview first, then bails out early
    /// if no heightmap was found -- so the postfix must not assume either is set.
    /// </summary>
    [HarmonyPatch(typeof(TerrainComp), "Awake")]
    internal static class TerrainCompAwakePatch
    {
        private static void Postfix(TerrainComp __instance)
        {
            if (!TrailEngine.Active || __instance.m_hmap == null)
            {
                return;
            }

            var pos = __instance.transform.position;
            var zone = ZoneSystem.GetZone(pos);
            TrailEngine.OnZoneLoaded(zone.x, zone.y);
        }
    }
}
