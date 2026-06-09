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
    /// Maps to stock <see cref="LocationAndSituationParameter"/> when a body is
    /// given (body-scoped: completes when the active vessel is in the situation
    /// at that body), or stock <see cref="ReachSituation"/> when the body is
    /// omitted. Both self-monitor the active vessel — no host entity required.
    ///
    /// Exception: <c>"flyby"</c> maps to stock <see cref="EnterSOI"/> (entering
    /// the body's sphere of influence), which is what the generator targets.
    /// The raw ESCAPING situation would be too strict — a flyby that captures
    /// into orbit would never tick.
    /// </summary>
    public sealed class SituationPrimitive : IContractPrimitive
    {
        public string Kind => "situation";

        public ContractParameter Build(JObject spec)
        {
            string situationStr = (string)spec["situation"];
            if (string.IsNullOrEmpty(situationStr))
                throw new FormatException("situation primitive missing 'situation'");

            string bodyName = (string)spec["body"];
            CelestialBody body = null;
            if (!string.IsNullOrEmpty(bodyName))
            {
                body = FlightGlobals.GetBodyByName(bodyName);
                if (body == null)
                    throw new FormatException($"situation primitive: unknown body '{bodyName}'");
            }

            if (situationStr == "flyby")
            {
                if (body == null)
                    throw new FormatException("flyby situation requires a body");
                return new EnterSOI(body);
            }

            Vessel.Situations situation = ParseSituation(situationStr);
            if (body == null)
                return new ReachSituation(situation, situationStr);

            // The third arg is a cosmetic noun ("Land your <noun> on <body>");
            // completion uses targetBody + targetSituation, not the noun.
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
