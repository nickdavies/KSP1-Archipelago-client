// Hardcoded port of future_expansions/AP_KSC_Sites/generate.py.
//
// BODIES — per-body cluster position picked from the AP_KSC_Probe
// equator scan (see generate.py for picking rationale).  FACILITIES —
// the 9 Squad KSC buildings, their meter offsets in the cluster's
// east/north frame, vertical offsets, and intended world-heading.
// Constants (decal radius, cluster offset, etc.) are also ported.
//
// When phase 3 lands, the BODIES lookup is replaced by reading a single
// row from AP slot_data; everything else here stays put.

using System.Collections.Generic;

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
        public string Model;
        public string Label;
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

        // 14 alien bodies.  Kerbin is intentionally omitted: it already
        // has the stock KSC.  When AP_KSC_ACTIVE_BODY is unset or set to
        // "Kerbin", the selector short-circuits and leaves stock alone.
        public static readonly BodySpec[] BODIES = new[]
        {
            new BodySpec { Name = "Tylo",   Lat =   0.0, Lon =  -30.0, TerrainAltM =  588.2 },
            new BodySpec { Name = "Moho",   Lat =   0.0, Lon =  163.0, TerrainAltM = 1055.0 },
            new BodySpec { Name = "Pol",    Lat =   0.0, Lon = -112.0, TerrainAltM =  969.5 },
            new BodySpec { Name = "Laythe", Lat =   0.0, Lon = -163.0, TerrainAltM =  731.8 },
            new BodySpec { Name = "Dres",   Lat =   0.0, Lon = -164.0, TerrainAltM =  224.4 },
            new BodySpec { Name = "Bop",    Lat =   0.0, Lon = -137.0, TerrainAltM = 6416.2 },
            new BodySpec { Name = "Eeloo",  Lat =   0.0, Lon =  115.0, TerrainAltM = 1535.9 },
            new BodySpec { Name = "Mun",    Lat =   0.0, Lon = -111.0, TerrainAltM = 2908.8 },
            // Greater Flats is already perfectly flat — skip decal.
            new BodySpec { Name = "Minmus", Lat =   0.0, Lon =  -17.0, TerrainAltM =    0.0, SkipMapDecal = true },
            // Off-equator mesa: +536 m vs equator pick, thinner atmosphere
            // helps Eve ascent.
            new BodySpec { Name = "Eve",    Lat = -25.0, Lon = -159.0, TerrainAltM = 6140.0 },
            new BodySpec { Name = "Ike",    Lat =   0.0, Lon =   73.0, TerrainAltM = 3179.0 },
            new BodySpec { Name = "Vall",   Lat =   0.0, Lon =  -64.0, TerrainAltM = 1222.2 },
            new BodySpec { Name = "Gilly",  Lat =   0.0, Lon =   30.0, TerrainAltM = 3925.5 },
            new BodySpec { Name = "Duna",   Lat =   0.0, Lon =  -19.0, TerrainAltM =  417.9 },
        };

        // Index FACILITIES[0] is LaunchPad.  KK auto-creates the
        // GroupCenter at the first PlaceStatic call's lat/lng, so
        // keeping LaunchPad first puts the group origin at the pad
        // (functionally equivalent to a cluster-centred origin —
        // building positions are absolute either way).
        public static readonly FacilitySpec[] FACILITIES = new[]
        {
            new FacilitySpec { Model = "KSC_LaunchPad_level_3",               Label = "LaunchPad",        EastM =    0.00, NorthM =    0.00, UpM =  0.00, HeadingDeg = 90.4 },
            new FacilitySpec { Model = "KSC_VehicleAssemblyBuilding_level_3", Label = "VAB",              EastM = -649.98, NorthM =    4.53, UpM = -0.53, HeadingDeg =  0.4 },
            new FacilitySpec { Model = "KSC_MissionControl_level_3",          Label = "MissionControl",   EastM = -598.70, NorthM =  234.30, UpM = -0.50, HeadingDeg = 90.4 },
            new FacilitySpec { Model = "KSC_TrackingStation_level_3",         Label = "TrackingStation",  EastM = -502.22, NorthM = -314.05, UpM = -0.53, HeadingDeg = 90.4 },
            new FacilitySpec { Model = "KSC_SpaceplaneHangar_level_3",        Label = "SPH",              EastM = -798.17, NorthM =  265.38, UpM = -0.61, HeadingDeg = 90.4 },
            new FacilitySpec { Model = "KSC_Runway_level_3",                  Label = "Runway",           EastM = -596.56, NorthM =  494.92, UpM = -0.49, HeadingDeg = 90.4 },
            new FacilitySpec { Model = "KSC_ResearchAndDevelopment_level_3",  Label = "RandD",            EastM = -901.37, NorthM = -195.79, UpM = -0.69, HeadingDeg = 90.4 },
            new FacilitySpec { Model = "KSC_AstronautComplex_level_3",        Label = "AstronautComplex", EastM = -949.54, NorthM =   64.36, UpM = -0.77, HeadingDeg = 90.4 },
            new FacilitySpec { Model = "KSC_Administration_level_3",          Label = "Administration",   EastM =-1088.99, NorthM =   65.50, UpM = -0.84, HeadingDeg =  0.4 },
        };

        public static bool TryFindBody(string name, out BodySpec spec)
        {
            foreach (var b in BODIES)
            {
                if (b.Name == name)
                {
                    spec = b;
                    return true;
                }
            }
            spec = default(BodySpec);
            return false;
        }
    }
}
