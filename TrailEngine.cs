using System.Collections.Generic;
using UnityEngine;

namespace AntTrails
{
    /// <summary>
    /// Server/host-side wear simulation. Owns every counter; clients only report where
    /// they walked.
    ///
    /// Nothing here touches terrain. The server cannot: it has no Heightmap and no
    /// TerrainComp anywhere near a player on a dedicated host. It decides what each tile
    /// should look like and offers that to whichever peers have the tile's zone loaded,
    /// and they paint it. See <see cref="TrailNetwork"/>.
    /// </summary>
    internal static class TrailEngine
    {
        /// <summary>
        /// Minimum change in dirt intensity before we bother repainting. Each repaint
        /// re-serializes a whole zone's terrain blob to every peer, so this is the main
        /// lever keeping a mod that fires on every footstep from flooding the network.
        /// </summary>
        private const float RepaintEpsilon = 0.06f;

        /// <summary>
        /// Ceiling on the dirty backlog. On a dedicated server nobody may be in range to
        /// paint for a long stretch, and without a cap this set would grow for the life of
        /// the session. Refusing new entries is the cheap choice: the tiles are still in the
        /// store and get re-marked next time their value moves.
        /// </summary>
        private const int MaxDirtyTiles = 8192;

        /// <summary>
        /// Seconds to wait for a peer's ack before offering a tile again. Covers a peer that
        /// was handed a batch while its zone was still loading.
        /// </summary>
        private const float PendingTimeoutSeconds = 15f;

        /// <summary>Zone radius assumed for our own player. Vanilla default is 2, plus one
        /// for the zone being walked into.</summary>
        private const int DefaultNearZones = 3;

        /// <summary>Stands in for "us" in the viewer list; never a real ZNet peer id.</summary>
        private const long LocalViewerId = long.MinValue;

        private static readonly HashSet<long> Dirty = new HashSet<long>();

        /// <summary>Tile -> realtime it was last offered to a peer, awaiting an ack.</summary>
        private static readonly Dictionary<long, float> Pending = new Dictionary<long, float>();

        /// <summary>
        /// Tile -> the dirt intensity we last *sent*, acked or not. PaintedR only moves on an
        /// ack, which is right for deciding what still needs painting but wrong for telling a
        /// client what it should expect to find on the ground: a dropped ack would leave
        /// PaintedR reading low, and the client would mistake our own paint for a player's and
        /// pin the tile. Session-scoped on purpose -- across a restart PaintedR is the honest
        /// answer, and an in-flight batch is long gone.
        /// </summary>
        private static readonly Dictionary<long, float> Offered = new Dictionary<long, float>();

        private static readonly List<Viewer> Viewers = new List<Viewer>();
        private static readonly Dictionary<long, List<long>> ZoneBatches = new Dictionary<long, List<long>>();
        private static readonly List<TerrainPainter.TilePaint> SendBuffer = new List<TerrainPainter.TilePaint>();

        /// <summary>Keys matching SendBuffer by index, so a dispatched tile can be recorded.</summary>
        private static readonly List<long> SendKeys = new List<long>();
        private static readonly List<TerrainPainter.TilePaint> Packet = new List<TerrainPainter.TilePaint>();
        private static readonly List<long> ExpiredBuffer = new List<long>();

        private static float _flushTimer;
        private static float _sweepTimer;

        /// <summary>A peer that has terrain loaded somewhere, and how far around itself.</summary>
        private struct Viewer
        {
            public long PeerId;
            public int ZoneX;
            public int ZoneZ;
            public int NearZones;
        }

        internal static bool Active { get; private set; }

        internal static void Begin(string worldName)
        {
            TrailStore.Load(worldName);
            Dirty.Clear();
            Pending.Clear();
            Offered.Clear();
            _flushTimer = 0f;
            _sweepTimer = 0f;
            Active = true;
        }

        internal static void End()
        {
            if (!Active)
            {
                return;
            }

            TrailStore.Save(force: true);
            TrailStore.Clear();
            Dirty.Clear();
            Pending.Clear();
            Offered.Clear();
            Active = false;
        }

        private static double DayLengthSeconds =>
            EnvMan.instance != null && EnvMan.instance.m_dayLengthSec > 0
                ? EnvMan.instance.m_dayLengthSec
                : 1800.0;

        internal static double WorldTime =>
            ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0.0;

        /// <summary>
        /// Brings a tile's decaying charge up to the current time. Unfinished tiles bleed
        /// progress quickly (FormationWindowDays); finished paths bleed slowly and stop at
        /// ResidualTrace. Stone never changes.
        /// </summary>
        private static void Advance(ref TileState t, double now)
        {
            double elapsed = now - t.LastTouch;
            if (elapsed <= 0.0)
            {
                // World time can jump backwards when loading an older save. Re-anchor
                // rather than crediting the tile with negative decay.
                t.LastTouch = now;
                return;
            }

            if (t.Stage == 2)
            {
                t.LastTouch = now;
                return;
            }

            float days = (float)(elapsed / DayLengthSeconds);
            float full = Mathf.Max(1f, AntTrailsConfig.StepsToPath.Value);

            float minCharge;
            float leakPerDay;

            if (t.Stage == 1)
            {
                float floor = Mathf.Clamp01(AntTrailsConfig.ResidualTrace.Value);
                minCharge = full * floor;
                leakPerDay = full * (1f - floor) / Mathf.Max(0.01f, AntTrailsConfig.RevertDays.Value);
            }
            else
            {
                minCharge = 0f;
                leakPerDay = full / Mathf.Max(0.01f, AntTrailsConfig.FormationWindowDays.Value);
            }

            t.Charge = Mathf.Max(minCharge, t.Charge - days * leakPerDay);
            t.LastTouch = now;
        }

        /// <summary>Dirt intensity (paint mask red channel) this tile currently warrants.</summary>
        private static float DesiredR(in TileState t)
        {
            float full = Mathf.Max(1f, AntTrailsConfig.StepsToPath.Value);
            return Mathf.Clamp01(t.Charge / full);
        }

        private static void MarkIfChanged(long key, in TileState t)
        {
            if (Dirty.Count >= MaxDirtyTiles && !Dirty.Contains(key))
            {
                return;
            }

            if (t.Stage == 2)
            {
                if (!t.PaintedStone)
                {
                    Dirty.Add(key);
                }
                return;
            }

            float target = DesiredR(t);

            // Always repaint the extremes exactly, so a path reaches full dirt and a fully
            // decayed tile reaches bare ground instead of stalling an epsilon short.
            bool atLimit = (target >= 0.999f && t.PaintedR < 0.999f)
                        || (target <= 0.001f && t.PaintedR > 0.001f);

            if (atLimit || Mathf.Abs(target - t.PaintedR) >= RepaintEpsilon)
            {
                Dirty.Add(key);
            }
        }

        /// <summary>
        /// Credits one crossing to each reported tile. Called on the server from the
        /// step RPC; <paramref name="tiles"/> is flat (x, z) pairs.
        /// </summary>
        internal static void RegisterSteps(int[] tiles)
        {
            if (!Active || !AntTrailsConfig.Enabled.Value || tiles == null)
            {
                return;
            }

            double now = WorldTime;
            float full = Mathf.Max(1f, AntTrailsConfig.StepsToPath.Value);
            float stoneSteps = AntTrailsConfig.StoneSteps.Value;
            float stoneChance = AntTrailsConfig.StoneChance.Value;

            for (int i = 0; i + 1 < tiles.Length; i += 2)
            {
                long key = TrailStore.TileKey(tiles[i], tiles[i + 1]);

                if (!TrailStore.TryGet(key, out var t))
                {
                    t = new TileState { LastTouch = now };
                }

                Advance(ref t, now);

                if (t.Stage == 2)
                {
                    // Stone is terminal. Keep counting traffic for stats, change nothing else.
                    t.TotalSteps += 1f;
                    TrailStore.Set(key, t);
                    continue;
                }

                t.Charge = Mathf.Min(full, t.Charge + 1f);
                t.TotalSteps += 1f;

                if (t.Stage == 0 && t.Charge >= full)
                {
                    t.Stage = 1;
                    AntTrailsPlugin.LogVerbose($"Tile {tiles[i]},{tiles[i + 1]} became a dirt path.");
                }

                if (t.Stage == 1 && t.TotalSteps >= stoneSteps && Random.value < stoneChance)
                {
                    t.Stage = 2;
                    AntTrailsPlugin.LogVerbose($"Tile {tiles[i]},{tiles[i + 1]} cobbled over to stone.");
                }

                TrailStore.Set(key, t);
                MarkIfChanged(key, t);
            }
        }

        /// <summary>
        /// A zone's terrain just spawned. Its tiles may have been decaying unwatched for
        /// days of world time; bring them current and repaint whatever drifted.
        /// </summary>
        internal static void OnZoneLoaded(int zx, int zz)
        {
            if (!Active || !AntTrailsConfig.Enabled.Value)
            {
                return;
            }

            long zoneKey = TrailStore.ZoneKey(zx, zz);
            if (!TrailStore.Zones.TryGetValue(zoneKey, out var keys) || keys.Count == 0)
            {
                return;
            }

            double now = WorldTime;
            foreach (var key in keys)
            {
                if (!TrailStore.TryGet(key, out var t))
                {
                    continue;
                }

                Advance(ref t, now);
                TrailStore.Set(key, t);
                MarkIfChanged(key, t);
            }

            AntTrailsPlugin.LogVerbose($"Reconciled {keys.Count} tile(s) in zone {zx},{zz}.");
        }

        /// <summary>
        /// Re-evaluates decay for tiles in zones somebody can actually see.
        ///
        /// "Somebody" means a connected peer, not ZoneSystem.IsZoneLoaded: on a dedicated
        /// server the zones the server itself keeps loaded sit at the world origin and have
        /// nothing to do with where anyone is standing. This sweep also does the work that
        /// OnZoneLoaded does for a host -- catching a zone up after it has been unwatched --
        /// because TerrainComp.Awake never fires near a player on a dedicated server.
        /// </summary>
        private static void Sweep()
        {
            CollectViewers();
            if (Viewers.Count == 0)
            {
                return;
            }

            double now = WorldTime;

            foreach (var zone in TrailStore.Zones)
            {
                TrailStore.SplitZone(zone.Key, out int zx, out int zz);
                if (!AnyViewerNear(zx, zz))
                {
                    continue;
                }

                foreach (var key in zone.Value)
                {
                    if (!TrailStore.TryGet(key, out var t))
                    {
                        continue;
                    }

                    // Stone never decays, but it can still be waiting on its first paint.
                    if (t.Stage != 2)
                    {
                        Advance(ref t, now);
                        TrailStore.Set(key, t);
                    }

                    MarkIfChanged(key, t);
                }
            }
        }

        /// <summary>
        /// Everyone who has terrain loaded somewhere: every connected peer, plus ourselves
        /// when this is a host-and-play session. A dedicated server contributes nobody of its
        /// own, because its live zones sit at the origin regardless of where players are.
        /// </summary>
        private static void CollectViewers()
        {
            Viewers.Clear();

            var znet = ZNet.instance;
            if (znet == null)
            {
                return;
            }

            if (Player.m_localPlayer != null)
            {
                var localZone = ZoneSystem.GetZone(Player.m_localPlayer.transform.position);
                Viewers.Add(new Viewer
                {
                    PeerId = LocalViewerId,
                    ZoneX = localZone.x,
                    ZoneZ = localZone.y,
                    NearZones = DefaultNearZones
                });
            }

            foreach (var peer in znet.GetPeers())
            {
                if (peer == null || !peer.IsReady())
                {
                    continue;
                }

                // A peer's own simulation distance is how many zones it keeps live around
                // itself; one extra covers the zone it is walking into. Over-reaching only
                // costs an unacked packet, so err wide.
                int near = Mathf.Max(1, peer.m_simulationDistance.NearSimulationDistance) + 1;

                var peerZone = ZoneSystem.GetZone(peer.GetRefPos());
                Viewers.Add(new Viewer
                {
                    PeerId = peer.m_uid,
                    ZoneX = peerZone.x,
                    ZoneZ = peerZone.y,
                    NearZones = near
                });
            }
        }

        private static bool AnyViewerNear(int zx, int zz)
        {
            for (int i = 0; i < Viewers.Count; i++)
            {
                if (ZoneInRange(Viewers[i], zx, zz))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ZoneInRange(Viewer viewer, int zx, int zz)
        {
            return Mathf.Abs(viewer.ZoneX - zx) <= viewer.NearZones
                && Mathf.Abs(viewer.ZoneZ - zz) <= viewer.NearZones;
        }

        internal static void Tick(float dt)
        {
            if (!Active || !AntTrailsConfig.Enabled.Value)
            {
                return;
            }

            _sweepTimer += dt;
            if (_sweepTimer >= AntTrailsConfig.DecaySweepSeconds.Value)
            {
                _sweepTimer = 0f;
                Sweep();
            }

            _flushTimer += dt;
            if (_flushTimer >= AntTrailsConfig.FlushIntervalSeconds.Value)
            {
                _flushTimer = 0f;
                Flush();
            }
        }

        /// <summary>
        /// Offers every dirty tile to the peers that have its zone loaded. Nothing is
        /// recorded as painted here: only a peer's ack retires a tile, because the server
        /// cannot see the terrain and has no way to know a send landed.
        /// </summary>
        private static void Flush()
        {
            if (Dirty.Count == 0)
            {
                return;
            }

            CollectViewers();
            if (Viewers.Count == 0)
            {
                return;
            }

            float nowReal = Time.time;
            ExpirePending(nowReal);

            // Group by zone so a peer gets one packet per zone rather than one per tile.
            ZoneBatches.Clear();
            int candidates = 0;

            foreach (var key in Dirty)
            {
                if (Pending.ContainsKey(key))
                {
                    continue;
                }

                TrailStore.SplitTile(key, out int tx, out int tz);
                long zoneKey = TrailStore.ZoneKeyForTile(tx, tz);

                if (!ZoneBatches.TryGetValue(zoneKey, out var list))
                {
                    list = new List<long>();
                    ZoneBatches[zoneKey] = list;
                }

                list.Add(key);
                candidates++;
            }

            if (candidates == 0)
            {
                return;
            }

            int offered = 0;
            foreach (var batch in ZoneBatches)
            {
                TrailStore.SplitZone(batch.Key, out int zx, out int zz);
                offered += OfferZone(zx, zz, batch.Value, nowReal);
            }

            if (offered > 0)
            {
                AntTrailsPlugin.LogVerbose(
                    $"Offered {offered} tile(s); {Dirty.Count} dirty, {Pending.Count} awaiting ack.");
            }
            else
            {
                // The dedicated-server failure this design exists to fix: counters moving,
                // terrain never changing. Say so rather than going quiet about it.
                AntTrailsPlugin.LogVerbose(
                    $"{candidates} dirty tile(s) but no peer has their zone loaded; deferring.");
            }
        }

        /// <summary>
        /// Hands one zone's dirty tiles to every peer near it. Returns how many distinct
        /// tiles went out; 0 means nobody was in range and the tiles stay dirty untouched.
        /// </summary>
        private static int OfferZone(int zx, int zz, List<long> keys, float nowReal)
        {
            SendBuffer.Clear();
            SendKeys.Clear();

            foreach (var key in keys)
            {
                if (!TrailStore.TryGet(key, out var t))
                {
                    continue;
                }

                TrailStore.SplitTile(key, out int tx, out int tz);
                bool stone = t.Stage == 2;

                // What the ground should read if nothing but us has touched it. The client
                // compares this against the real paint mask to work out whether a player's
                // hoe got there first -- see TerrainPainter.ApplyBatch.
                float prevR = t.PaintedR;
                if (Offered.TryGetValue(key, out float lastSent) && lastSent > prevR)
                {
                    prevR = lastSent;
                }

                SendKeys.Add(key);
                SendBuffer.Add(new TerrainPainter.TilePaint
                {
                    X = tx,
                    Z = tz,
                    R = stone ? 1f : DesiredR(t),
                    PrevR = prevR,
                    Stone = stone
                });
            }

            if (SendBuffer.Count == 0)
            {
                return 0;
            }

            int inRange = 0;
            for (int i = 0; i < Viewers.Count; i++)
            {
                if (ZoneInRange(Viewers[i], zx, zz))
                {
                    inRange++;
                }
            }

            if (inRange == 0)
            {
                return 0;
            }

            // Mark before dispatching: a local apply acks synchronously, and would otherwise
            // be undone by the marking that followed it.
            foreach (var key in keys)
            {
                Pending[key] = nowReal;
            }

            for (int i = 0; i < SendKeys.Count; i++)
            {
                Offered[SendKeys[i]] = SendBuffer[i].R;
            }

            for (int v = 0; v < Viewers.Count; v++)
            {
                var viewer = Viewers[v];
                if (!ZoneInRange(viewer, zx, zz))
                {
                    continue;
                }

                for (int start = 0; start < SendBuffer.Count; start += TrailNetwork.MaxTilesPerPacket)
                {
                    int len = Mathf.Min(TrailNetwork.MaxTilesPerPacket, SendBuffer.Count - start);

                    Packet.Clear();
                    for (int i = 0; i < len; i++)
                    {
                        Packet.Add(SendBuffer[start + i]);
                    }

                    if (viewer.PeerId == LocalViewerId)
                    {
                        // Host-and-play: we are also the peer standing there. No round trip.
                        ConfirmPainted(TerrainPainter.ApplyTiles(Packet));
                    }
                    else
                    {
                        TrailNetwork.SendPaint(viewer.PeerId, Packet);
                    }
                }
            }

            return SendBuffer.Count;
        }

        /// <summary>
        /// A peer reported that it applied these tiles. This ack is the only thing that
        /// retires a tile from the dirty set -- assuming a send succeeded is exactly how
        /// trails go missing without a word in the log.
        /// </summary>
        internal static void ConfirmPainted(ICollection<long> keys)
        {
            if (!Active || keys == null)
            {
                return;
            }

            foreach (var key in keys)
            {
                Pending.Remove(key);
                Dirty.Remove(key);

                if (!TrailStore.TryGet(key, out var t))
                {
                    continue;
                }

                if (t.Stage == 2)
                {
                    t.PaintedStone = true;
                    t.PaintedR = 1f;
                }
                else
                {
                    t.PaintedR = DesiredR(t);
                }

                // The offered value only covers a send we never heard back about. Now that
                // PaintedR has caught up it says nothing extra, and keeping it would let this
                // dictionary grow for the life of the session.
                if (Offered.TryGetValue(key, out float sent) && t.PaintedR >= sent - 0.001f)
                {
                    Offered.Remove(key);
                }

                TrailStore.Set(key, t);
            }
        }

        private static void ExpirePending(float nowReal)
        {
            if (Pending.Count == 0)
            {
                return;
            }

            ExpiredBuffer.Clear();
            foreach (var kv in Pending)
            {
                if (nowReal - kv.Value >= PendingTimeoutSeconds)
                {
                    ExpiredBuffer.Add(kv.Key);
                }
            }

            foreach (var key in ExpiredBuffer)
            {
                Pending.Remove(key);
            }
        }
    }
}
