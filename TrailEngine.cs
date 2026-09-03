using System.Collections.Generic;
using UnityEngine;

namespace AntTrails
{
    /// <summary>
    /// Server/host-side wear simulation. Owns every counter; clients only report where
    /// they walked. Nothing here touches terrain directly -- it decides what each tile
    /// should look like and hands the dirty set to <see cref="TerrainPainter"/>.
    /// </summary>
    internal static class TrailEngine
    {
        /// <summary>
        /// Minimum change in dirt intensity before we bother repainting. Each repaint
        /// re-serializes a whole zone's terrain blob to every peer, so this is the main
        /// lever keeping a mod that fires on every footstep from flooding the network.
        /// </summary>
        private const float RepaintEpsilon = 0.06f;

        private static readonly HashSet<long> Dirty = new HashSet<long>();

        private static float _flushTimer;
        private static float _sweepTimer;

        internal static bool Active { get; private set; }

        internal static void Begin(string worldName)
        {
            TrailStore.Load(worldName);
            Dirty.Clear();
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
            Active = false;
        }

        private static double DayLengthSeconds =>
            EnvMan.instance != null && EnvMan.instance.m_dayLengthSec > 0
                ? EnvMan.instance.m_dayLengthSec
                : 1200.0;

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

        /// <summary>Re-evaluates decay for tiles in loaded zones only.</summary>
        private static void Sweep()
        {
            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem == null)
            {
                return;
            }

            double now = WorldTime;

            foreach (var zone in TrailStore.Zones)
            {
                TrailStore.SplitZone(zone.Key, out int zx, out int zz);
                if (!zoneSystem.IsZoneLoaded(new Vector2i(zx, zz)))
                {
                    continue;
                }

                foreach (var key in zone.Value)
                {
                    if (!TrailStore.TryGet(key, out var t) || t.Stage == 2)
                    {
                        continue;
                    }

                    Advance(ref t, now);
                    TrailStore.Set(key, t);
                    MarkIfChanged(key, t);
                }
            }
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

        private static void Flush()
        {
            if (Dirty.Count == 0)
            {
                return;
            }

            var applied = TerrainPainter.Apply(Dirty);

            // Only tiles we actually dispatched are recorded as painted. Anything left
            // (zone not loaded, terrain refused it) stays dirty and retries next flush.
            foreach (var key in applied)
            {
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

                TrailStore.Set(key, t);
                Dirty.Remove(key);
            }

            if (applied.Count > 0)
            {
                AntTrailsPlugin.LogVerbose($"Flushed {applied.Count} tile(s); {Dirty.Count} deferred.");
            }
        }
    }
}
