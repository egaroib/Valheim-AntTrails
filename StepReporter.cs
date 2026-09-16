using System.Collections.Generic;
using UnityEngine;

namespace AntTrails
{
    /// <summary>
    /// Client side. Traces which 1x1m tiles the local player walks across and reports
    /// crossings to the server in batches.
    ///
    /// Sampling only credits the tile underfoot would leave gaps: at a run the player
    /// covers more than a metre between samples, so consecutive sampled tiles are not
    /// even adjacent and the trail comes out dotted. Instead each sample credits every
    /// tile on the line from the previous sample, so a route is traced continuously
    /// however fast it is travelled.
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

        /// <summary>Longest run of travel between two samples still treated as a walk, in metres.</summary>
        private const float MaxSegmentLength = 8f;

        /// <summary>Hard cap on tiles credited from one segment.</summary>
        private const int MaxSegmentTiles = 24;

        private static readonly HashSet<long> Pending = new HashSet<long>();

        private static float _sampleTimer;
        private static float _sendTimer;
        private static bool _haveLastPos;
        private static Vector3 _lastPos;

        internal static void Reset()
        {
            Pending.Clear();
            _sampleTimer = 0f;
            _sendTimer = 0f;
            _haveLastPos = false;
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
                _haveLastPos = false;
                return;
            }

            // Only real footfalls on real ground. Swimming, wading, and riding count for
            // nothing, and neither does standing on a built floor.
            if (!player.IsOnGround() || player.IsSwimming() || player.InWater() || player.InLiquid())
            {
                _haveLastPos = false;
                return;
            }

            var ground = player.GetLastGroundCollider();
            if (ground == null)
            {
                _haveLastPos = false;
                return;
            }

            // The game's own test for "terrain, not a building piece" (see FootStep.GetGroundMaterial).
            var hmap = ground.GetComponent<Heightmap>();
            if (hmap == null)
            {
                _haveLastPos = false;
                return;
            }

            var pos = player.transform.position;

            if (!_haveLastPos)
            {
                // Just landed, surfaced, or stepped off a floor. There is no previous
                // position to draw from, so credit only where we are standing.
                Credit(hmap, Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.z), pos.y);
            }
            else
            {
                TraceSegment(hmap, _lastPos, pos);
            }

            _haveLastPos = true;
            _lastPos = pos;
        }

        /// <summary>
        /// Credits every tile the straight line from <paramref name="from"/> to
        /// <paramref name="to"/> passes through, excluding the one it starts in --
        /// that tile was credited by the sample that ended there.
        /// </summary>
        private static void TraceSegment(Heightmap hmap, Vector3 from, Vector3 to)
        {
            int tx = Mathf.FloorToInt(from.x);
            int tz = Mathf.FloorToInt(from.z);
            int endX = Mathf.FloorToInt(to.x);
            int endZ = Mathf.FloorToInt(to.z);

            if (tx == endX && tz == endZ)
            {
                return; // never left the tile; standing still still costs nothing
            }

            float dx = to.x - from.x;
            float dz = to.z - from.z;

            // A portal, a teleport, or a hitching frame is not a walk. Credit where the
            // player actually ended up rather than ruling a line across the landscape.
            if (dx * dx + dz * dz > MaxSegmentLength * MaxSegmentLength)
            {
                Credit(hmap, endX, endZ, to.y);
                return;
            }

            int stepX = dx > 0f ? 1 : -1;
            int stepZ = dz > 0f ? 1 : -1;

            // Distance along the segment, as a fraction of its length, to the next tile
            // boundary on each axis and between successive boundaries after that.
            float tDeltaX = dx != 0f ? Mathf.Abs(1f / dx) : float.MaxValue;
            float tDeltaZ = dz != 0f ? Mathf.Abs(1f / dz) : float.MaxValue;

            float tMaxX = dx > 0f ? (tx + 1 - from.x) / dx
                        : dx < 0f ? (tx - from.x) / dx
                        : float.MaxValue;
            float tMaxZ = dz > 0f ? (tz + 1 - from.z) / dz
                        : dz < 0f ? (tz - from.z) / dz
                        : float.MaxValue;

            // Amanatides-Woo grid traversal: repeatedly cross whichever axis boundary
            // the segment reaches first. The step cap is a guard against float drift
            // leaving the walk unable to land exactly on the end tile.
            for (int i = 0; i < MaxSegmentTiles; i++)
            {
                if (tMaxX < tMaxZ)
                {
                    tx += stepX;
                    tMaxX += tDeltaX;
                }
                else
                {
                    tz += stepZ;
                    tMaxZ += tDeltaZ;
                }

                Credit(hmap, tx, tz, to.y);

                if (tx == endX && tz == endZ)
                {
                    return;
                }
            }
        }

        /// <summary>Queues one crossing of a tile, if the tile is one we are willing to wear.</summary>
        private static void Credit(Heightmap hmap, int tx, int tz, float y)
        {
            if (Pending.Count >= MaxPendingTiles)
            {
                return;
            }

            // Eligibility is tested at the tile centre, which is where the wear lands,
            // rather than wherever within the tile the player happened to be sampled.
            if (!IsEligible(hmap, new Vector3(tx + 0.5f, y, tz + 0.5f)))
            {
                return;
            }

            Pending.Add(TrailStore.TileKey(tx, tz));
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
