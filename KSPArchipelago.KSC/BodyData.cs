// Static geometry for the alien-KSC clone, ported from
// future_expansions/AP_KSC_Sites/generate.py.  FACILITIES — the 9 Squad
// KSC buildings, their metre offsets in the cluster's east/north frame,
// vertical offsets, and intended world-heading.  Plus the decal/cluster
// constants.  All of this is body-independent (cluster-relative), so it
// lives baked in the client.
//
// The per-body landing coordinate (lat/lon/terrain alt + map-decal flag)
// is NOT here: it arrives in slot_data as the `ksc_site` row for the
// chosen starting body and is carried in as a BodySpec (see
// KSPArchipelagoMod.HandleConnect → IStartingBodyHandler → ApplyServerBody).

namespace KSPArchipelago.KSC
{
    public struct BodySpec
    {
        public string Name;
        public double Lat;
        public double Lon;
        public double TerrainAltM;
        public bool   SkipMapDecal;
    }

    public struct FacilitySpec
    {
        // L3 model name. Kept for backwards-compat — the materialiser strips
        // the "_level_3" suffix to derive a ModelBase, then computes the
        // actual KK model name from AP-granted level (see Materialiser.cs).
        public string Model;
        public string Label;
        // KSP UpgradeableFacility id, e.g. "SpaceCenter/LaunchPad". Maps the
        // facility spec to CareerUpgradesManager so the materialiser knows
        // which AP-granted level to use for model selection.
        public string FacilityId;
        public double EastM;      // cluster-frame east offset before re-centring
        public double NorthM;     // cluster-frame north offset before re-centring
        public double UpM;        // vertical offset preserving real Kerbin KSC heights
        public double HeadingDeg; // CW from north (0=N, 90=E)
    }

    public static class BodyData
    {
        // Decal radius — sized to cover the runway tips at D ≈ 1310 m
        // from cluster centre.  See generate.py for derivation.
        public const double DECAL_RADIUS_M = 2000.0;

        // Cluster centroid offset relative to LaunchPad (real KSC layout).
        // Subtracted from each building's raw east/north so the cluster is
        // centred on the GroupCenter rather than the LaunchPad.
        public const double CLUSTER_OFFSET_EAST  = -596.0;
        public const double CLUSTER_OFFSET_NORTH =  103.0;

        // Uniform lift added to each building's natural up_m so foundations
        // clear the flattened terrain.
        public const double BUILDING_LIFT_M = 1.0;

        // In-game KK applies ~93% of the configured HeightMapDeformity
        // (user measurement, see generate.py).  1/0.93 ≈ 1.075 compensates.
        public const double CALIBRATION_MULTIPLIER = 1.075;

        // Heightmap registered by Heightmaps/APKSC_KerbinCurve.cfg.
        public const string KSC_HEIGHTMAP = "APKSC_KerbinCurve";

        // Kerbin's radius — used as the reference curvature target.  Read
        // at runtime from FlightGlobals to avoid drift if Kopernicus or
        // similar mods replace Kerbin; this constant is kept only as a
        // documentation reference.
        public const double R_KERBIN_M = 600000.0;

        // Index FACILITIES[0] is LaunchPad.  KK auto-creates the
        // GroupCenter at the first PlaceStatic call's lat/lng, so
        // keeping LaunchPad first puts the group origin at the pad
        // (functionally equivalent to a cluster-centred origin —
        // building positions are absolute either way).
        public static readonly FacilitySpec[] FACILITIES = new[]
        {
            new FacilitySpec { Model = "KSC_LaunchPad_level_3",               Label = "LaunchPad",        FacilityId = "SpaceCenter/LaunchPad",              EastM =    0.00, NorthM =    0.00, UpM =  0.00, HeadingDeg = 90.4 },
            new FacilitySpec { Model = "KSC_VehicleAssemblyBuilding_level_3", Label = "VAB",              FacilityId = "SpaceCenter/VehicleAssemblyBuilding", EastM = -649.98, NorthM =    4.53, UpM = -0.53, HeadingDeg =  0.4 },
            new FacilitySpec { Model = "KSC_MissionControl_level_3",          Label = "MissionControl",   FacilityId = "SpaceCenter/MissionControl",         EastM = -598.70, NorthM =  234.30, UpM = -0.50, HeadingDeg = 90.4 },
            new FacilitySpec { Model = "KSC_TrackingStation_level_3",         Label = "TrackingStation",  FacilityId = "SpaceCenter/TrackingStation",        EastM = -502.22, NorthM = -314.05, UpM = -0.53, HeadingDeg = 90.4 },
            new FacilitySpec { Model = "KSC_SpaceplaneHangar_level_3",        Label = "SPH",              FacilityId = "SpaceCenter/SpaceplaneHangar",       EastM = -798.17, NorthM =  265.38, UpM = -0.61, HeadingDeg = 90.4 },
            new FacilitySpec { Model = "KSC_Runway_level_3",                  Label = "Runway",           FacilityId = "SpaceCenter/Runway",                 EastM = -596.56, NorthM =  494.92, UpM = -0.49, HeadingDeg = 90.4 },
            new FacilitySpec { Model = "KSC_ResearchAndDevelopment_level_3",  Label = "RandD",            FacilityId = "SpaceCenter/ResearchAndDevelopment", EastM = -901.37, NorthM = -195.79, UpM = -0.69, HeadingDeg = 90.4 },
            new FacilitySpec { Model = "KSC_AstronautComplex_level_3",        Label = "AstronautComplex", FacilityId = "SpaceCenter/AstronautComplex",       EastM = -949.54, NorthM =   64.36, UpM = -0.77, HeadingDeg = 90.4 },
            new FacilitySpec { Model = "KSC_Administration_level_3",          Label = "Administration",   FacilityId = "SpaceCenter/Administration",         EastM =-1088.99, NorthM =   65.50, UpM = -0.84, HeadingDeg =  0.4 },
        };
    }
}
