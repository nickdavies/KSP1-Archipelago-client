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
    public sealed class CollectSciencePrimitive : IContractPrimitive
    {
        public string Kind => "collect_science";

        public ContractParameter Build(JObject spec)
        {
            string bodyName = (string)spec["body"];
            if (string.IsNullOrEmpty(bodyName))
                throw new FormatException("collect_science primitive missing 'body'");
            CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
            if (body == null)
                throw new FormatException($"collect_science primitive: unknown body '{bodyName}'");
            return new CollectScience(body, ParseLocation((string)spec["location"]));
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
