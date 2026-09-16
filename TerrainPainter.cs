using System.Collections.Generic;
using UnityEngine;

namespace AntTrails
{
    /// <summary>
    /// Client side. Turns the server's "this tile should look like this" into terrain paint.
    ///
    /// Resolution happens here rather than on the server because this is the peer that
    /// actually has the zone loaded: Heightmap.s_heightmaps, the TerrainComp, and the real
    /// paint mask all live on whoever is standing there. The server only knows tile
    /// coordinates.
    ///
    /// Vanilla paints by spawning a TerrainOp, which re-serializes the whole zone's terrain
    /// blob into a ZDO once per operation. Doing that per footstep would be ruinous, so a
    /// whole batch is applied to each TerrainComp before a single Save and rebuild.
    /// </summary>
    internal static class TerrainPainter
    {
        /// <summary>One tile's target appearance, as decided by the server.</summary>
        internal struct TilePaint
        {
            public int X;
            public int Z;
            public float R;

            /// <summary>
            /// Dirt intensity the server last asked this tile for. Ground darker than this
            /// was darkened by someone else, and ApplyBatch treats it as a floor.
            /// </summary>
            public float PrevR;

            public bool Stone;
        }

        /// <summary>
        /// How far the red channel may read above what the server last asked for before it
        /// counts as someone else's work. A value we wrote comes back quantised to 1/255 --
        /// the paint mask is an RGBA32 texture -- and a round trip through the terrain blob
        /// costs a little more, so this sits comfortably clear of both.
        /// </summary>
        private const float PaintReadbackTolerance = 0.02f;

        private static readonly List<Heightmap> HmapBuffer = new List<Heightmap>();

        /// <summary>
        /// Applies every tile in the batch whose terrain this peer owns, and returns those
        /// tiles' keys. A tile is reported as handled only when we own a TerrainComp covering
        /// it -- anything else stays the server's problem and will be offered again.
        /// </summary>
        internal static HashSet<long> ApplyTiles(List<TilePaint> tiles)
        {
            var handled = new HashSet<long>();

            if (tiles == null || Heightmap.s_heightmaps == null || Heightmap.s_heightmaps.Count == 0)
            {
                return handled;
            }

            var batches = new Dictionary<TerrainComp, List<PaintEntry>>();

            foreach (var tile in tiles)
            {
                // Eligibility is tested at the tile centre, which is where the wear lands.
                var center = new Vector3(tile.X + 0.5f, 0f, tile.Z + 0.5f);

                HmapBuffer.Clear();
                Heightmap.FindHeightmap(center, 1f, HmapBuffer);
                if (HmapBuffer.Count == 0)
                {
                    continue; // zone not loaded here; some other peer will get this one
                }

                bool owned = false;

                // A tile on a zone seam is a shared vertex on two or four heightmaps.
                // Paint every one of them we own, the same way vanilla terrain ops do, or
                // the path shows a one-pixel gap at every zone boundary.
                foreach (var hmap in HmapBuffer)
                {
                    // Mirrors TerrainComp.PaintCleared, which shifts by -0.5 before
                    // resolving a world position to a paint-mask vertex.
                    hmap.WorldToVertexMask(new Vector3(tile.X, 0f, tile.Z), out int vx, out int vy);

                    int stride = hmap.m_width + 1;
                    if (vx < 0 || vy < 0 || vx >= stride || vy >= stride)
                    {
                        continue;
                    }

                    var comp = hmap.GetAndCreateTerrainCompiler();
                    if (comp == null || comp.m_nview == null || !comp.m_nview.IsValid())
                    {
                        continue;
                    }

                    // Only the owner may write a TerrainComp's blob. Acking a tile we do not
                    // own is what would make the server believe a trail was painted when it
                    // was not -- the exact failure this whole path exists to avoid.
                    if (!comp.m_nview.IsOwner() || !comp.m_initialized || comp.m_hmap == null)
                    {
                        continue;
                    }

                    if (!batches.TryGetValue(comp, out var list))
                    {
                        list = new List<PaintEntry>();
                        batches[comp] = list;
                    }

                    list.Add(new PaintEntry
                    {
                        X = vx,
                        Y = vy,
                        R = tile.R,
                        PrevR = tile.PrevR,
                        Stone = tile.Stone
                    });
                    owned = true;
                }

                if (owned)
                {
                    handled.Add(TrailStore.TileKey(tile.X, tile.Z));
                }
            }

            foreach (var batch in batches)
            {
                ApplyBatch(batch.Key, batch.Value);
            }

            return handled;
        }

        /// <summary>
        /// Applies a whole batch to one TerrainComp, then saves and rebuilds once. The single
        /// save is the entire reason this batches at all.
        /// </summary>
        private static void ApplyBatch(TerrainComp comp, List<PaintEntry> entries)
        {
            var hmap = comp.m_hmap;
            int stride = comp.m_width + 1;
            int changed = 0;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            foreach (var e in entries)
            {
                if (e.X < 0 || e.Y < 0 || e.X >= stride || e.Y >= stride)
                {
                    continue;
                }

                int idx = e.Y * stride + e.X;

                // The heightmap texture is the merged result of world generation and every
                // applied terrain op, so it is the honest source for "what is here now".
                Color current = hmap.GetPaintMask(e.X, e.Y);

                // Never touch farmland. Walking across a turnip patch should not pave it.
                if (current.g > 0.5f)
                {
                    continue;
                }

                // Never overwrite existing stone, whether a player laid it or we did.
                if (current.b > 0.5f)
                {
                    continue;
                }

                Color next = current;
                if (e.Stone)
                {
                    // Stone writes the blue channel, so no dirt floor can veto it: a route
                    // worn all the way to cobbles cobbles over whoever laid the dirt first.
                    next.r = 0f;
                    next.g = 0f;
                    next.b = 1f;
                }
                else
                {
                    // Ground darker than the server last asked for was darkened by somebody
                    // else -- a player's hoe, or another mod. Vanilla's Heightmap.IsCleared
                    // reads the red channel as a hard threshold at 0.5 and ignores alpha
                    // entirely, so writing our own fractional wear over a hoed path would
                    // both erase it and hand the tile straight back to the grass system.
                    //
                    // So: we may darken a tile further, never lighten one we did not darken.
                    // Our own trails are unaffected, because on those the ground tracks what
                    // we last sent and the floor stays at zero -- decay still works.
                    float floor = current.r > e.PrevR + PaintReadbackTolerance ? current.r : 0f;
                    next.r = Mathf.Max(floor, Mathf.Clamp01(e.R));
                }

                // Alpha carries vegetation clearing, and lava level in the Ashlands.
                // Preserving it is what keeps this from melting or de-scorching terrain.
                next.a = current.a;

                // Compare against the merged mask rather than our own layer. A tile pinned
                // at a player's value is offered again every time the wear model creeps past
                // its repaint epsilon, and without this each of those would re-serialize the
                // zone's terrain blob and kick the grass system for no visible change.
                if (Mathf.Abs(current.r - next.r) < 0.001f
                    && Mathf.Abs(current.g - next.g) < 0.001f
                    && Mathf.Abs(current.b - next.b) < 0.001f)
                {
                    continue;
                }

                comp.m_modifiedPaint[idx] = true;
                comp.m_paintMask[idx] = next;
                changed++;

                float worldX = hmap.transform.position.x + (e.X - stride / 2);
                float worldZ = hmap.transform.position.z + (e.Y - stride / 2);
                if (worldX < minX) minX = worldX;
                if (worldX > maxX) maxX = worldX;
                if (worldZ < minZ) minZ = worldZ;
                if (worldZ > maxZ) maxZ = worldZ;
            }

            if (changed == 0)
            {
                return;
            }

            comp.Save();
            hmap.Poke(delayed: 0);

            if (ClutterSystem.instance != null)
            {
                var center = new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);
                float radius = Mathf.Max(maxX - minX, maxZ - minZ) * 0.5f + 2f;
                ClutterSystem.instance.ResetGrass(center, radius);
            }

            AntTrailsPlugin.LogVerbose($"Painted {changed} vertex/vertices on terrain at {hmap.transform.position}.");
        }

        private struct PaintEntry
        {
            public int X;
            public int Y;
            public float R;
            public float PrevR;
            public bool Stone;
        }
    }
}
