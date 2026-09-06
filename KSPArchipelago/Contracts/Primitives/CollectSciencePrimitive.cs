using System;
using Contracts;
using Contracts.Parameters;
using Newtonsoft.Json.Linq;
using KSPArchipelago.Contracts.Parameters;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind":"collect_science", "body":"Mun", "location":"space" }</c>
    /// (location ∈ {any, space, surface})
    ///
    /// Completes when science from that body reaches R&amp;D, <b>recovered OR
    /// transmitted</b> (GameEvents.OnScienceRecieved / OnTriggeredDataTransmission).
    ///
    ///   - <c>space</c> / <c>surface</c> bind the situation, and map to stock
    ///     <see cref="CollectScience"/>. The <c>space</c> variant is the cheap
    ///     "phone home" contract — no round trip. It is also what the home body
    ///     always gets, because a surface/any contract at home is satisfied by
    ///     a goo canister on the launchpad.
    ///   - <c>any</c> drops the situation filter entirely and maps to
    ///     <see cref="BodyScienceParameter"/>: ANY science-yielding thing from
    ///     the body, from any situation, at any value.
    /// </summary>
    public sealed class CollectSciencePrimitive : ContractPrimitiveBase<CollectSciencePrimitive.Spec>
    {
        public override string Kind => "collect_science";

        /// <summary>Where the science has to come from. <c>Any</c> has no stock equivalent.</summary>
        public enum ScienceWhere { Any, Space, Surface }

        public struct Spec { public string Body; public ScienceWhere Where; }

        protected override Spec Parse(JObject spec)
        {
            string bodyName = (string)spec["body"];
            if (string.IsNullOrEmpty(bodyName))
                throw new FormatException("collect_science primitive missing 'body'");
            return new Spec { Body = bodyName, Where = ParseWhere((string)spec["location"]) };
        }

        protected override ContractParameter BuildFrom(Spec p)
        {
            CelestialBody body = FlightGlobals.GetBodyByName(p.Body);
            if (body == null)
                throw new FormatException($"collect_science primitive: unknown body '{p.Body}'");
            switch (p.Where)
            {
                case ScienceWhere.Space:   return new CollectScience(body, BodyLocation.Space);
                case ScienceWhere.Surface: return new CollectScience(body, BodyLocation.Surface);
                default:                   return new BodyScienceParameter(p.Body);
            }
        }

        private static ScienceWhere ParseWhere(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "any":     return ScienceWhere.Any;
                case "space":   return ScienceWhere.Space;
                case "surface": return ScienceWhere.Surface;
                default:
                    throw new FormatException(
                        $"collect_science primitive: unknown location '{s}'");
            }
        }
    }
}
