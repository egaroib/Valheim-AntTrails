using System.Collections.Generic;
using UnityEngine;

namespace AntTrails
{
    /// <summary>
    /// Turns the engine's dirty tiles into terrain paint.
    ///
    /// Vanilla paints by spawning a TerrainOp, which serializes the whole zone's terrain
    /// blob into a ZDO once per operation (TerrainComp.Save). Doing that per footstep would
    /// be ruinous, so instead we batch every changed tile in a zone into one custom RPC
    /// delivered to that zone's TerrainComp owner, which applies them all and saves once.
    /// </summary>
    internal static class TerrainPainter
    {
        internal const string PaintRpc = "AntTrails_Paint";

        private static readonly List<Heightmap> HmapBuffer = new List<Heightmap>();

        /// <summary>
        /// Dispatches paint for as many dirty tiles as currently have loaded terrain.
        /// Returns the tiles actually dispatched; the rest stay dirty and retry later.
        /// </summary>
        internal static HashSet<long> Apply(HashSet<long> dirty)
        {
            var applied = new HashSet<long>();

            if (Heightmap.s_heightmaps == null || Heightmap.s_heightmaps.Count == 0)
            {
                return applied;
            }

            // TerrainComp -> flat (vertexX, vertexY, r, stone) entries.
            var batches = new Dictionary<TerrainComp, List<PaintEntry>>();

            foreach (var key in dirty)
            {
                TrailStore.SplitTile(key, out int tx, out int tz);
                if (!TrailStore.TryGet(key, out var t))
                {
                    continue;
                }

                var center = new Vector3(tx + 0.5f, 0f, tz + 0.5f);

                HmapBuffer.Clear();
                Heightmap.FindHeightmap(center, 1f, HmapBuffer);
                if (HmapBuffer.Count == 0)
                {
                    continue; // zone not loaded anywhere we can see; retry next flush
                }

                bool stone = t.Stage == 2;
                float r = stone ? 1f : Mathf.Clamp01(t.Charge / Mathf.Max(1f, AntTrailsConfig.StepsToPath.Value));

                bool dispatched = false;

                // A tile on a zone seam is a shared vertex on two or four heightmaps.
                // Paint every one of them, the same way vanilla terrain ops do, or the
                // path shows a one-pixel gap at every zone boundary.
                foreach (var hmap in HmapBuffer)
                {
                    // Mirrors TerrainComp.PaintCleared, which shifts by -0.5 before
                    // resolving a world position to a paint-mask vertex.
                    hmap.WorldToVertexMask(new Vector3(tx, 0f, tz), out int vx, out int vy);

                    int stride = hmap.m_width + 1;
                    if (vx < 0 || vy < 0 || vx >= stride || vy >= stride)
                    {
                        continue;
                    }

                    var comp = hmap.GetAndCreateTerrainCompiler();
                    if (comp == null)
                    {
                        continue;
                    }

                    if (!batches.TryGetValue(comp, out var list))
                    {
                        list = new List<PaintEntry>();
                        batches[comp] = list;
                    }

                    list.Add(new PaintEntry { X = vx, Y = vy, R = r, Stone = stone });
                    dispatched = true;
                }

                if (dispatched)
                {
                    applied.Add(key);
                }
            }

            foreach (var batch in batches)
            {
                Send(batch.Key, batch.Value);
            }

            return applied;
        }

        private static void Send(TerrainComp comp, List<PaintEntry> entries)
        {
            var nview = comp.m_nview;
            if (nview == null || !nview.IsValid())
            {
                return;
            }

            var pkg = new ZPackage();
            pkg.Write(entries.Count);
            foreach (var e in entries)
            {
                pkg.Write(e.X);
                pkg.Write(e.Y);
                pkg.Write(e.R);
                pkg.Write(e.Stone);
            }

            // Routes to whichever peer owns this zone's terrain; delivered locally if
            // that happens to be us.
            nview.InvokeRPC(PaintRpc, pkg);
        }

        /// <summary>
        /// Runs on the TerrainComp owner. Applies a whole batch, then saves and rebuilds once.
        /// Eligibility is enforced here rather than on the server, because the owner is the
        /// peer guaranteed to have the real paint mask in memory.
        /// </summary>
        internal static void HandlePaintRpc(TerrainComp comp, long sender, ZPackage pkg)
        {
            if (comp == null || comp.m_nview == null || !comp.m_nview.IsOwner())
            {
                return;
            }

            var hmap = comp.m_hmap;
            if (hmap == null || !comp.m_initialized)
            {
                return;
            }

            int count = pkg.ReadInt();
            int stride = comp.m_width + 1;
            int changed = 0;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            for (int i = 0; i < count; i++)
            {
                int vx = pkg.ReadInt();
                int vy = pkg.ReadInt();
                float r = pkg.ReadSingle();
                bool stone = pkg.ReadBool();

                if (vx < 0 || vy < 0 || vx >= stride || vy >= stride)
                {
                    continue;
                }

                int idx = vy * stride + vx;

                // The heightmap texture is the merged result of world generation and every
                // applied terrain op, so it is the honest source for "what is here now".
                Color current = hmap.GetPaintMask(vx, vy);

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
                if (stone)
                {
                    next.r = 0f;
                    next.g = 0f;
                    next.b = 1f;
                }
                else
                {
                    next.r = Mathf.Clamp01(r);
                }

                // Alpha carries vegetation clearing, and lava level in the Ashlands.
                // Preserving it is what keeps this from melting or de-scorching terrain.
                next.a = current.a;

                if (comp.m_modifiedPaint[idx]
                    && Mathf.Abs(comp.m_paintMask[idx].r - next.r) < 0.001f
                    && Mathf.Abs(comp.m_paintMask[idx].b - next.b) < 0.001f)
                {
                    continue;
                }

                comp.m_modifiedPaint[idx] = true;
                comp.m_paintMask[idx] = next;
                changed++;

                float worldX = hmap.transform.position.x + (vx - stride / 2);
                float worldZ = hmap.transform.position.z + (vy - stride / 2);
                if (worldX < minX) minX = worldX;
                if (worldX > maxX) maxX = worldX;
                if (worldZ < minZ) minZ = worldZ;
                if (worldZ > maxZ) maxZ = worldZ;
            }

            if (changed == 0)
            {
                return;
            }

            // One save and one rebuild for the whole batch. This is the entire reason
            // the mod batches at all.
            comp.Save();
            hmap.Poke(delayed: false);

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
            public bool Stone;
        }
    }
}
