using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;

namespace AntTrails
{
    /// <summary>
    /// One 1x1m world tile's wear state. Held only by the server/host.
    /// </summary>
    internal struct TileState
    {
        /// <summary>Decaying traffic charge, 0..StepsToPath. Drives how dark the dirt is.</summary>
        public float Charge;

        /// <summary>Lifetime crossings. Never decays; gates the stone upgrade.</summary>
        public float TotalSteps;

        /// <summary>World time (ZNet seconds) Charge was last brought up to date.</summary>
        public double LastTouch;

        /// <summary>0 = forming, 1 = established dirt, 2 = stone (permanent).</summary>
        public byte Stage;

        /// <summary>Dirt intensity we last asked the terrain for, so we can skip no-op repaints.</summary>
        public float PaintedR;

        /// <summary>Whether the terrain has already been told to go stone here.</summary>
        public bool PaintedStone;
    }

    /// <summary>
    /// Server-side tile state, persisted per world.
    ///
    /// This does not live in the world .db. Valheim's terrain ZDO stores the *result*
    /// (the paint mask); the wear counters that produced it are ours, so they go in a
    /// sidecar file under BepInEx/config rather than anywhere the game's save scanner looks.
    /// Losing the sidecar loses progress, not terrain -- paths already painted stay painted.
    /// </summary>
    internal static class TrailStore
    {
        private const int FileVersion = 1;

        private static readonly Dictionary<long, TileState> Tiles = new Dictionary<long, TileState>();

        /// <summary>Tile keys grouped by zone, so decay sweeps can skip unloaded zones cheaply.</summary>
        private static readonly Dictionary<long, HashSet<long>> ByZone = new Dictionary<long, HashSet<long>>();

        private static string _path;
        private static bool _dirty;

        internal static int Count => Tiles.Count;
        internal static Dictionary<long, HashSet<long>> Zones => ByZone;

        internal static long TileKey(int x, int z) => ((long)x << 32) | (uint)z;
        internal static void SplitTile(long key, out int x, out int z)
        {
            x = (int)(key >> 32);
            z = (int)(key & 0xFFFFFFFFL);
        }

        internal static long ZoneKey(int zx, int zz) => ((long)zx << 32) | (uint)zz;
        internal static void SplitZone(long key, out int zx, out int zz)
        {
            zx = (int)(key >> 32);
            zz = (int)(key & 0xFFFFFFFFL);
        }

        /// <summary>Zone a tile belongs to. Mirrors ZoneSystem.GetZone, which uses 64m zones offset by 32.</summary>
        internal static long ZoneKeyForTile(int x, int z)
        {
            int zx = (int)Math.Floor((x + 32.0) / 64.0);
            int zz = (int)Math.Floor((z + 32.0) / 64.0);
            return ZoneKey(zx, zz);
        }

        internal static bool TryGet(long key, out TileState state) => Tiles.TryGetValue(key, out state);

        internal static void Set(long key, in TileState state)
        {
            if (!Tiles.ContainsKey(key))
            {
                long zone = ZoneKeyForTile((int)(key >> 32), (int)(key & 0xFFFFFFFFL));
                if (!ByZone.TryGetValue(zone, out var set))
                {
                    set = new HashSet<long>();
                    ByZone[zone] = set;
                }
                set.Add(key);
            }

            Tiles[key] = state;
            _dirty = true;
        }

        internal static void Clear()
        {
            Tiles.Clear();
            ByZone.Clear();
            _path = null;
            _dirty = false;
        }

        private static string PathFor(string worldName)
        {
            var dir = Path.Combine(Paths.ConfigPath, "AntTrails");
            Directory.CreateDirectory(dir);
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                worldName = worldName.Replace(c, '_');
            }
            return Path.Combine(dir, worldName + ".trails");
        }

        internal static void Load(string worldName)
        {
            Clear();
            _path = PathFor(worldName);

            if (!File.Exists(_path))
            {
                AntTrailsPlugin.LogInfo($"No trail data for world '{worldName}'; starting fresh.");
                return;
            }

            try
            {
                using (var stream = File.OpenRead(_path))
                using (var r = new BinaryReader(stream))
                {
                    int version = r.ReadInt32();
                    if (version != FileVersion)
                    {
                        AntTrailsPlugin.LogWarning(
                            $"Trail data for '{worldName}' is version {version}, expected {FileVersion}. " +
                            "Ignoring it; wear counters restart but painted terrain is unaffected.");
                        return;
                    }

                    int count = r.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        int x = r.ReadInt32();
                        int z = r.ReadInt32();
                        var s = new TileState
                        {
                            Charge = r.ReadSingle(),
                            TotalSteps = r.ReadSingle(),
                            LastTouch = r.ReadDouble(),
                            Stage = r.ReadByte(),
                            PaintedR = r.ReadSingle(),
                            PaintedStone = r.ReadBoolean()
                        };
                        Set(TileKey(x, z), s);
                    }
                }

                _dirty = false;
                AntTrailsPlugin.LogInfo($"Loaded {Tiles.Count} worn tile(s) for world '{worldName}'.");
            }
            catch (Exception e)
            {
                AntTrailsPlugin.LogError(
                    $"Could not read trail data at {_path}: {e.Message}. " +
                    "Wear counters restart; painted terrain is unaffected.");
                Clear();
                _path = PathFor(worldName);
            }
        }

        internal static void Save(bool force = false)
        {
            if (_path == null || (!_dirty && !force))
            {
                return;
            }

            try
            {
                var tmp = _path + ".tmp";
                using (var stream = File.Create(tmp))
                using (var w = new BinaryWriter(stream))
                {
                    w.Write(FileVersion);
                    w.Write(Tiles.Count);
                    foreach (var kv in Tiles)
                    {
                        SplitTile(kv.Key, out int x, out int z);
                        w.Write(x);
                        w.Write(z);
                        w.Write(kv.Value.Charge);
                        w.Write(kv.Value.TotalSteps);
                        w.Write(kv.Value.LastTouch);
                        w.Write(kv.Value.Stage);
                        w.Write(kv.Value.PaintedR);
                        w.Write(kv.Value.PaintedStone);
                    }
                }

                // Replace only after a complete write, so a crash mid-save cannot
                // truncate the previous good file.
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
                File.Move(tmp, _path);

                _dirty = false;
                AntTrailsPlugin.LogVerbose($"Saved {Tiles.Count} worn tile(s).");
            }
            catch (Exception e)
            {
                AntTrailsPlugin.LogError($"Could not write trail data to {_path}: {e.Message}");
            }
        }
    }
}
