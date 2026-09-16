using BepInEx.Configuration;

namespace AntTrails
{
    /// <summary>
    /// All tunables. Anything that changes world state is admin-only, so the server
    /// dictates it and clients cannot diverge -- see references/multiplayer.md.
    /// Purely local preferences (sampling rate, logging) stay client-editable.
    /// </summary>
    internal static class AntTrailsConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> StepsToPath;
        internal static ConfigEntry<float> FormationWindowDays;
        internal static ConfigEntry<float> RevertDays;
        internal static ConfigEntry<float> ResidualTrace;
        internal static ConfigEntry<float> StoneSteps;
        internal static ConfigEntry<float> StoneChance;
        internal static ConfigEntry<bool> RespectBuildPrivilege;
        internal static ConfigEntry<float> FlushIntervalSeconds;
        internal static ConfigEntry<float> DecaySweepSeconds;
        internal static ConfigEntry<float> SampleIntervalSeconds;
        internal static ConfigEntry<bool> VerboseLogging;

        internal static void Bind(ConfigFile cfg)
        {
            var admin = new ConfigurationManagerAttributes { IsAdminOnly = true };

            Enabled = cfg.Bind("General", "Enabled", true,
                new ConfigDescription(
                    "Master switch. When false, no trails form, decay, or upgrade. " +
                    "Existing painted terrain is left exactly as it is.",
                    null, admin));

            StepsToPath = cfg.Bind("Formation", "StepsToPath", 25f,
                new ConfigDescription(
                    "Tile crossings needed to turn one 1x1m tile into a full dirt path. " +
                    "Counts from every player pooled together, not per player. " +
                    "The tile tints progressively on the way there; grass clears at the halfway mark.",
                    new AcceptableValueRange<float>(1f, 2000f), admin));

            FormationWindowDays = cfg.Bind("Formation", "FormationWindowDays", 12f,
                new ConfigDescription(
                    "In-game days of no traffic for an unfinished tile to lose ALL its accumulated " +
                    "progress. Progress leaks at a flat StepsToPath/FormationWindowDays per day, so " +
                    "this is really a rate gate: a tile crossed less often than that never gains " +
                    "ground at all, however long you keep at it. Lower it and only frantic traffic " +
                    "leaves a mark; raise it and routes wear in from occasional use.",
                    new AcceptableValueRange<float>(0.25f, 200f), admin));

            RevertDays = cfg.Bind("Decay", "RevertDays", 30f,
                new ConfigDescription(
                    "In-game days of no traffic for a finished dirt path to fade all the way down " +
                    "to ResidualTrace. Much slower than FormationWindowDays: paths are easy to lose " +
                    "before they exist and hard to lose afterwards.",
                    new AcceptableValueRange<float>(0.1f, 2000f), admin));

            ResidualTrace = cfg.Bind("Decay", "ResidualTrace", 0.3f,
                new ConfigDescription(
                    "Floor a finished path fades to, as a fraction of full dirt. Above 0 means " +
                    "abandoned routes stay visible forever as faint ghost trails. " +
                    "Below 0.5 the grass grows back over them. Set to 0 to let paths vanish entirely.",
                    new AcceptableValueRange<float>(0f, 1f), admin));

            StoneSteps = cfg.Bind("Stone", "StoneSteps", 400f,
                new ConfigDescription(
                    "Lifetime crossings on one tile before it becomes eligible to cobble over into " +
                    "stone. Unlike formation progress this total never decays, so it measures how " +
                    "heavily travelled the tile has been across the whole life of the world.",
                    new AcceptableValueRange<float>(1f, 100000f), admin));

            StoneChance = cfg.Bind("Stone", "StoneChance", 0.01f,
                new ConfigDescription(
                    "Chance per crossing, once eligible, that a tile turns to stone. Low on purpose: " +
                    "it should speckle stone through a well-worn road, not pave it uniformly. " +
                    "Stone is permanent -- it never decays and only a hoe or pickaxe removes it.",
                    new AcceptableValueRange<float>(0f, 1f), admin));

            RespectBuildPrivilege = cfg.Bind("Eligibility", "RespectBuildPrivilege", true,
                new ConfigDescription(
                    "Skip tiles inside a workbench or ward radius, keeping bases pristine. " +
                    "Set false if you want trails to form around and through settlements.",
                    null, admin));

            FlushIntervalSeconds = cfg.Bind("Performance", "FlushIntervalSeconds", 5f,
                new ConfigDescription(
                    "Seconds between pushing accumulated paint changes to the terrain. Every flush " +
                    "makes the affected zone re-serialize its entire terrain blob to all peers, so " +
                    "raising this trades responsiveness for bandwidth.",
                    new AcceptableValueRange<float>(1f, 120f), admin));

            DecaySweepSeconds = cfg.Bind("Performance", "DecaySweepSeconds", 60f,
                new ConfigDescription(
                    "Seconds between re-evaluating loaded tiles for decay. Tiles in unloaded zones " +
                    "cost nothing; they catch up from elapsed world time when the zone next loads.",
                    new AcceptableValueRange<float>(5f, 600f), admin));

            SampleIntervalSeconds = cfg.Bind("Performance", "SampleIntervalSeconds", 0.25f,
                new ConfigDescription(
                    "How often this client samples its own position. Tiles between two samples " +
                    "are filled in, so this does not decide whether fast travel registers -- only " +
                    "how closely the traced line follows a curving route. Local setting.",
                    new AcceptableValueRange<float>(0.05f, 2f)));

            VerboseLogging = cfg.Bind("General", "VerboseLogging", false,
                "Log per-tile detail. Extremely noisy; for debugging only.");
        }
    }
}
