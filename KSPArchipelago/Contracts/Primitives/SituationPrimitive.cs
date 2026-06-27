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
    public sealed class SituationPrimitive : ContractPrimitiveBase<SituationPrimitive.Spec>
    {
        public override string Kind => "situation";

        public struct Spec
        {
            public string SituationStr;
            public string BodyName;            // optional (required only for flyby)
            public bool IsFlyby;
            public Vessel.Situations Situation; // valid when !IsFlyby
        }

        protected override Spec Parse(JObject spec)
        {
            string situationStr = (string)spec["situation"];
            if (string.IsNullOrEmpty(situationStr))
                throw new FormatException("situation primitive missing 'situation'");
            string bodyName = (string)spec["body"];
            bool isFlyby = situationStr == "flyby";
            if (isFlyby && string.IsNullOrEmpty(bodyName))
                throw new FormatException("flyby situation requires a body");
            return new Spec
            {
                SituationStr = situationStr,
                BodyName = bodyName,
                IsFlyby = isFlyby,
                // ParseSituation throws on an unknown situation (skip for flyby,
                // which isn't a Vessel.Situations value).
                Situation = isFlyby ? default(Vessel.Situations) : ParseSituation(situationStr),
            };
        }

        protected override ContractParameter BuildFrom(Spec p)
        {
            CelestialBody body = null;
            if (!string.IsNullOrEmpty(p.BodyName))
            {
                body = FlightGlobals.GetBodyByName(p.BodyName);
                if (body == null)
                    throw new FormatException($"situation primitive: unknown body '{p.BodyName}'");
            }

            if (p.IsFlyby)
                return new EnterSOI(body);   // body required by Parse, so non-null

            if (body == null)
                return new ReachSituation(p.Situation, p.SituationStr);

            // The third arg is a cosmetic noun ("Land your <noun> on <body>");
            // completion uses targetBody + targetSituation, not the noun.
            return new LocationAndSituationParameter(body, p.Situation, "vessel");
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
