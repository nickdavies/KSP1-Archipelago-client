using System;
using Contracts;
using Contracts.Parameters;
using FinePrint.Contracts.Parameters;
using Newtonsoft.Json.Linq;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind": "situation", "situation": "landed", "body": "Mun" }</c>
    ///
    /// Maps to stock <see cref="LocationAndSituationParameter"/> when a body
    /// is given (body-scoped: completes only when the active vessel is in the
    /// situation at that body), or stock <see cref="ReachSituation"/> when the
    /// body is omitted (body-agnostic). Both self-monitor the active vessel
    /// via onVesselSituationChange — no host contract entity required (verified
    /// against the KSP assembly: LocationAndSituationParameter reads
    /// FlightGlobals.ActiveVessel + targetBody/targetSituation directly).
    /// </summary>
    public sealed class SituationPrimitive : IContractPrimitive
    {
        public string Kind => "situation";

        public ContractParameter Build(JObject spec)
        {
            string situationStr = (string)spec["situation"];
            if (string.IsNullOrEmpty(situationStr))
                throw new FormatException("situation primitive missing 'situation'");
            Vessel.Situations situation = ParseSituation(situationStr);

            string bodyName = (string)spec["body"];
            if (string.IsNullOrEmpty(bodyName))
                return new ReachSituation(situation, situationStr);

            CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
            if (body == null)
                throw new FormatException($"situation primitive: unknown body '{bodyName}'");

            // KSP builds the title as e.g. "Land your <noun> on <body>" — the
            // noun is the vessel, not a repeat of the situation/body (which
            // produced "Land your landed at Mun on Mun"). Purely cosmetic; the
            // completion check uses targetBody + targetSituation, not the noun.
            return new LocationAndSituationParameter(body, situation, "vessel");
        }

        private static Vessel.Situations ParseSituation(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "landed":               return Vessel.Situations.LANDED;
                case "splashed":
                case "splashdown":           return Vessel.Situations.SPLASHED;
                case "prelaunch":            return Vessel.Situations.PRELAUNCH;
                case "flying":               return Vessel.Situations.FLYING;
                case "suborbital":
                case "sub_orbital":          return Vessel.Situations.SUB_ORBITAL;
                case "orbit":
                case "orbiting":             return Vessel.Situations.ORBITING;
                case "escaping":             return Vessel.Situations.ESCAPING;
                case "docked":               return Vessel.Situations.DOCKED;
                default:
                    throw new FormatException($"situation primitive: unknown situation '{s}'");
            }
        }
    }
}
