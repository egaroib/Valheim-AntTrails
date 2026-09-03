using System.Collections.Generic;
using UnityEngine;

namespace AntTrails
{
    /// <summary>
    /// Client side. Watches which 1x1m tile the local player is standing on and reports
    /// crossings to the server in batches.
    ///
    /// Reporting on tile *entry* rather than continuously is what stops a player idling at
    /// a workbench from boring a hole in the ground: standing still is one crossing, no
    /// matter how long you stand.
    /// </summary>
    internal static class StepReporter
    {
        private const int MaxPendingTiles = 512;

        /// <summary>Seconds between batches. Independent of sampling rate; this is the network cost.</summary>
        private const float SendIntervalSeconds = 2f;

        private static readonly HashSet<long> Pending = new HashSet<long>();

        private static float _sampleTimer;
        private static float _sendTimer;
        private static bool _haveLastTile;
        private static int _lastX;
        private static int _lastZ;

        internal static void Reset()
        {
            Pending.Clear();
            _sampleTimer = 0f;
            _sendTimer = 0f;
            _haveLastTile = false;
        }

        internal static void Tick(float dt)
        {
            if (!AntTrailsConfig.Enabled.Value)
            {
                return;
            }

            _sampleTimer += dt;
            if (_sampleTimer >= AntTrailsConfig.SampleIntervalSeconds.Value)
            {
                _sampleTimer = 0f;
                Sample();
            }

            _sendTimer += dt;
            if (_sendTimer >= SendIntervalSeconds)
            {
                _sendTimer = 0f;
                Send();
            }
        }

        private static void Sample()
        {
            var player = Player.m_localPlayer;
            if (player == null || player.IsDead())
            {
                _haveLastTile = false;
                return;
            }

            // Only real footfalls on real ground. Swimming, wading, and riding count for
            // nothing, and neither does standing on a built floor.
            if (!player.IsOnGround() || player.IsSwimming() || player.InWater() || player.InLiquid())
            {
                _haveLastTile = false;
                return;
            }

            var ground = player.GetLastGroundCollider();
            if (ground == null)
            {
                _haveLastTile = false;
                return;
            }

            // The game's own test for "terrain, not a building piece" (see FootStep.GetGroundMaterial).
            var hmap = ground.GetComponent<Heightmap>();
            if (hmap == null)
            {
                _haveLastTile = false;
                return;
            }

            var pos = player.transform.position;
            int tx = Mathf.FloorToInt(pos.x);
            int tz = Mathf.FloorToInt(pos.z);

            if (_haveLastTile && tx == _lastX && tz == _lastZ)
            {
                return; // still on the same tile; not a new crossing
            }

            _haveLastTile = true;
            _lastX = tx;
            _lastZ = tz;

            if (!IsEligible(hmap, pos))
            {
                return;
            }

            if (Pending.Count < MaxPendingTiles)
            {
                Pending.Add(TrailStore.TileKey(tx, tz));
            }
        }

        private static bool IsEligible(Heightmap hmap, Vector3 pos)
        {
            // Molten ground is not a candidate for a footpath.
            if (hmap.IsLava(pos))
            {
                return false;
            }

            if (AntTrailsConfig.RespectBuildPrivilege.Value && InsideBuildPrivilege(pos))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// True if the point sits inside any active workbench or ward radius. Checked on the
        /// client because that is the peer with those pieces loaded around it.
        /// </summary>
        private static bool InsideBuildPrivilege(Vector3 pos)
        {
            var areas = PrivateArea.m_allAreas;
            if (areas == null)
            {
                return false;
            }

            for (int i = 0; i < areas.Count; i++)
            {
                var area = areas[i];
                if (area != null && area.IsEnabled() && area.IsInside(pos, 0f))
                {
                    return true;
                }
            }

            return false;
        }

        private static void Send()
        {
            if (Pending.Count == 0 || ZRoutedRpc.instance == null)
            {
                return;
            }

            var pkg = new ZPackage();
            pkg.Write(Pending.Count);
            foreach (var key in Pending)
            {
                TrailStore.SplitTile(key, out int x, out int z);
                pkg.Write(x);
                pkg.Write(z);
            }

            AntTrailsPlugin.LogVerbose($"Reporting {Pending.Count} tile crossing(s).");
            Pending.Clear();

            // No explicit target: this overload routes to the server, and dispatches
            // locally when we are the server.
            ZRoutedRpc.instance.InvokeRoutedRPC(TrailNetwork.StepsRpc, pkg);
        }
    }
}
