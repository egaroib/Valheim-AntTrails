namespace AntTrails
{
    /// <summary>
    /// Client-to-server step reporting. Registered once per session, since ZRoutedRpc is
    /// rebuilt on every ZNet.Awake and its handler table rejects duplicate names.
    /// </summary>
    internal static class TrailNetwork
    {
        internal const string StepsRpc = "AntTrails_Steps";

        private static bool _registered;

        internal static void Register()
        {
            if (_registered || ZRoutedRpc.instance == null)
            {
                return;
            }

            ZRoutedRpc.instance.Register<ZPackage>(StepsRpc, RPC_Steps);
            _registered = true;
            AntTrailsPlugin.LogInfo("Registered step-reporting RPC.");
        }

        internal static void Reset()
        {
            _registered = false;
        }

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
    }
}
