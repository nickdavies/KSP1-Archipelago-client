using System;
using Contracts;
using FinePrint;
using FinePrint.Utilities;
using Newtonsoft.Json.Linq;
using KSPArchipelago.Contracts.Parameters;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind":"survey_waypoint", "body":"Mun", "experiment":"temperatureScan",
    /// "lat":15.0, "seed":12345 }</c>
    ///
    /// Picks a deterministic water-free ground site from (body, lat-bound, seed)
    /// — identical on every reload — builds a FinePrint surface
    /// <see cref="Waypoint"/> there (mirroring stock <c>SurveyContract.Generate</c>,
    /// Assembly-CSharp decompiled :886005), and wraps stock
    /// <see cref="FinePrint.Contracts.Parameters.SurveyWaypointParameter"/> at the
    /// GROUND flight band. Completion is stock <c>GameEvents.OnExperimentDeployed</c>
    /// at the waypoint — running the experiment there is sufficient (no transmit
    /// or recovery). The stock param submits the waypoint to
    /// <c>WaypointManager</c> itself; <see cref="ApSurveyWaypointParameter"/> only
    /// ensures that submit happens under our generic host.
    ///
    /// <c>lat</c> is the seeded latitude BOUND in degrees (same convention as
    /// <c>surface_rescue</c>); <c>seed</c> drives the site pick. All four fields
    /// are required — a missing/invalid value is rejected as schema drift.
    /// </summary>
    public sealed class SurveyWaypointPrimitive : ContractPrimitiveBase<SurveyWaypointPrimitive.Spec>
    {
        public override string Kind => "survey_waypoint";

        public struct Spec
        {
            public string Body;
            public string Experiment;
            public double Lat;
            public int Seed;
        }

        protected override Spec Parse(JObject spec)
        {
            string body = (string)spec["body"];
            if (string.IsNullOrEmpty(body))
                throw new FormatException("survey_waypoint primitive missing 'body'");
            string experiment = (string)spec["experiment"];
            if (string.IsNullOrEmpty(experiment))
                throw new FormatException("survey_waypoint primitive missing 'experiment'");
            double? lat = spec.Value<double?>("lat");
            if (lat == null || lat.Value <= 0.0 || lat.Value > 90.0)
                throw new FormatException("survey_waypoint primitive missing/invalid 'lat'");
            long? seed = spec.Value<long?>("seed");
            if (seed == null)
                throw new FormatException("survey_waypoint primitive missing 'seed'");
            return new Spec
            {
                Body = body,
                Experiment = experiment,
                Lat = lat.Value,
                Seed = unchecked((int)seed.Value),
            };
        }

        protected override ContractParameter BuildFrom(Spec p)
        {
            CelestialBody body = FlightGlobals.GetBodyByName(p.Body);
            if (body == null)
                throw new FormatException($"survey_waypoint primitive: unknown body '{p.Body}'");
            double lat, lon;
            if (!SeededSites.PickSite(body, p.Seed, p.Lat, out lat, out lon))
                throw new FormatException(
                    $"survey_waypoint primitive: no terrain data yet for '{p.Body}'");
            // Mirror stock SurveyContract.Generate waypoint construction: the
            // fresh waypoint carries seed/id/index/celestialName + lat/lon; the
            // stock param's ProcessWaypoint fills the rest (isOnSurface, name,
            // altitudes) and submits it to WaypointManager.
            Waypoint wp = new Waypoint
            {
                seed = p.Seed,
                id = "report",                 // stock contract waypoint icon
                index = 0,
                celestialName = body.GetName(),
                latitude = lat,
                longitude = lon,
            };
            return new ApSurveyWaypointParameter(
                p.Experiment, "Perform a survey", body, wp, FlightBand.GROUND);
        }
    }
}
