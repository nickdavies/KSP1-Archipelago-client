using System;
using Contracts;
using Contracts.Parameters;
using Newtonsoft.Json.Linq;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind":"collect_science", "body":"Mun", "location":"space" }</c>
    ///
    /// Maps to stock <see cref="CollectScience"/> bound to a body location
    /// (space|surface): completes when science from that body+location is received,
    /// <b>recovered OR transmitted</b> (GameEvents.OnScienceRecieved /
    /// OnTriggeredDataTransmission). The <c>space</c> variant is the cheap "phone
    /// home" contract — no round trip. (The <c>surface</c> variant is the same stock
    /// param the <c>sample_return</c> primitive wraps.)
    /// </summary>
    public sealed class CollectSciencePrimitive : ContractPrimitiveBase<CollectSciencePrimitive.Spec>
    {
        public override string Kind => "collect_science";

        public struct Spec { public string Body; public BodyLocation Location; }

        protected override Spec Parse(JObject spec)
        {
            string bodyName = (string)spec["body"];
            if (string.IsNullOrEmpty(bodyName))
                throw new FormatException("collect_science primitive missing 'body'");
            return new Spec { Body = bodyName, Location = ParseLocation((string)spec["location"]) };
        }

        protected override ContractParameter BuildFrom(Spec p)
        {
            CelestialBody body = FlightGlobals.GetBodyByName(p.Body);
            if (body == null)
                throw new FormatException($"collect_science primitive: unknown body '{p.Body}'");
            return new CollectScience(body, p.Location);
        }

        private static BodyLocation ParseLocation(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "space":   return BodyLocation.Space;
                case "surface": return BodyLocation.Surface;
                default:
                    throw new FormatException(
                        $"collect_science primitive: unknown location '{s}'");
            }
        }
    }
}
