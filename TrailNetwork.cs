using System.Collections.Generic;

namespace AntTrails
{
    /// <summary>
    /// All AntTrails traffic, as routed RPCs registered once per session on every peer.
    ///
    /// Three messages, in a loop:
    ///   Steps    client -> server   "I walked across these tiles."
    ///   Paint    server -> client   "These tiles should look like this now."
    ///   PaintAck client -> server   "I own the terrain for these and have applied them."
    ///
    /// Paint deliberately does NOT go through the zone's TerrainComp ZNetView. A dedicated
    /// server has no TerrainComp -- and no Heightmap -- anywhere near a player, because
    /// ZoneSystem only builds live zones around ZNet.GetReferencePosition(), which stays at
    /// the origin when there is no local player. Peers get ghost zones, whose roots are
    /// destroyed as soon as they are generated. So the server cannot resolve a tile to a
    /// terrain object at all; it can only name the tile and let a peer that has it loaded
    /// do the resolving. See references/multiplayer.md.
    /// </summary>
    internal static class TrailNetwork
    {
        internal const string StepsRpc = "AntTrails_Steps";
        internal const string PaintRpc = "AntTrails_Paint";
        internal const string PaintAckRpc = "AntTrails_PaintAck";

        /// <summary>Tiles per packet. Keeps one flush from becoming one oversized message.</summary>
        internal const int MaxTilesPerPacket = 256;

        private static bool _registered;

        internal static void Register()
        {
            if (_registered || ZRoutedRpc.instance == null)
            {
                return;
            }

            ZRoutedRpc.instance.Register<ZPackage>(StepsRpc, RPC_Steps);
            ZRoutedRpc.instance.Register<ZPackage>(PaintRpc, RPC_Paint);
            ZRoutedRpc.instance.Register<ZPackage>(PaintAckRpc, RPC_PaintAck);
            _registered = true;
            AntTrailsPlugin.LogInfo("Registered step, paint, and paint-ack RPCs.");
        }

        internal static void Reset()
        {
            _registered = false;
        }

        /// <summary>Server side. Credits reported crossings.</summary>
        private static void RPC_Steps(long sender, ZPackage pkg)
        {
            // Only the peer holding the counters acts on these. Clients receive nothing.
            if (!TrailEngine.Active || pkg == null)
            {
                return;
            }

            int count = pkg.ReadInt();
            if (count <= 0 || count > 4096)
            {
                return;
            }

            var tiles = new int[count * 2];
            for (int i = 0; i < count; i++)
            {
                tiles[i * 2] = pkg.ReadInt();
                tiles[i * 2 + 1] = pkg.ReadInt();
            }

            TrailEngine.RegisterSteps(tiles);
        }

        /// <summary>
        /// Client side. Paints whatever of this batch we own the terrain for, and tells the
        /// server which ones those were. Tiles we cannot place -- zone not loaded here, or
        /// another peer owns that TerrainComp -- are simply not acked, and the server keeps
        /// them dirty for a later attempt.
        /// </summary>
        private static void RPC_Paint(long sender, ZPackage pkg)
        {
            // Paint flows server -> client only. If we are the server this is not for us.
            if (pkg == null || ZNet.instance == null || ZNet.instance.IsServer())
            {
                return;
            }

            int count = pkg.ReadInt();
            if (count <= 0 || count > MaxTilesPerPacket)
            {
                return;
            }

            var tiles = new List<TerrainPainter.TilePaint>(count);
            for (int i = 0; i < count; i++)
            {
                // Field order must match SendPaint exactly; an object initialiser evaluates
                // its assignments top to bottom, so these reads stay in wire order.
                tiles.Add(new TerrainPainter.TilePaint
                {
                    X = pkg.ReadInt(),
                    Z = pkg.ReadInt(),
                    R = pkg.ReadSingle(),
                    PrevR = pkg.ReadSingle(),
                    Stone = pkg.ReadBool()
                });
            }

            var handled = TerrainPainter.ApplyTiles(tiles);
            AntTrailsPlugin.LogVerbose($"Paint batch: {count} tile(s) offered, {handled.Count} applied here.");
            SendPaintAck(sender, handled);
        }

        /// <summary>Server side. Records what a client actually managed to paint.</summary>
        private static void RPC_PaintAck(long sender, ZPackage pkg)
        {
            if (!TrailEngine.Active || pkg == null)
            {
                return;
            }

            int count = pkg.ReadInt();
            if (count <= 0 || count > MaxTilesPerPacket)
            {
                return;
            }

            var keys = new List<long>(count);
            for (int i = 0; i < count; i++)
            {
                int x = pkg.ReadInt();
                int z = pkg.ReadInt();
                keys.Add(TrailStore.TileKey(x, z));
            }

            TrailEngine.ConfirmPainted(keys);
        }

        /// <summary>Server side. Offers one zone's worth of tiles to one peer.</summary>
        internal static void SendPaint(long targetPeerId, List<TerrainPainter.TilePaint> tiles)
        {
            if (ZRoutedRpc.instance == null || tiles.Count == 0)
            {
                return;
            }

            var pkg = new ZPackage();
            pkg.Write(tiles.Count);
            foreach (var t in tiles)
            {
                pkg.Write(t.X);
                pkg.Write(t.Z);
                pkg.Write(t.R);
                pkg.Write(t.PrevR);
                pkg.Write(t.Stone);
            }

            ZRoutedRpc.instance.InvokeRoutedRPC(targetPeerId, PaintRpc, pkg);
        }

        private static void SendPaintAck(long targetPeerId, HashSet<long> handled)
        {
            if (ZRoutedRpc.instance == null || handled.Count == 0)
            {
                return;
            }

            var pkg = new ZPackage();
            pkg.Write(handled.Count);
            foreach (var key in handled)
            {
                TrailStore.SplitTile(key, out int x, out int z);
                pkg.Write(x);
                pkg.Write(z);
            }

            ZRoutedRpc.instance.InvokeRoutedRPC(targetPeerId, PaintAckRpc, pkg);
        }
    }
}
