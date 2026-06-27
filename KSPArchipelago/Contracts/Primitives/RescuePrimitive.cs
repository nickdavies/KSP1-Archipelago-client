using System;
using Contracts;
using Newtonsoft.Json.Linq;
using KSPArchipelago.Contracts.Parameters;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind": "rescue", "body": "Jool", "sma": 17000000 }</c>
    ///
    /// Builds a <see cref="RescueKerbalParameter"/>, which SPAWNS a stranded
    /// Kerbal in orbit of the body and completes when they are recovered. This is
    /// the only primitive that creates world state rather than watching the
    /// player's vessel — the free-seat requirement rides as a separate
    /// has_any_part objective emitted by the generator.
    ///
    /// <c>sma</c> is the seeded orbit radius from the body centre, metres (same
    /// convention as <c>specific_orbit</c>); the generator picks it collision-safe
    /// against moon SOIs and models the rescue dv to it. Required — the generator
    /// always seeds it, so a missing/invalid value is rejected as schema drift.
    /// </summary>
    public sealed class RescuePrimitive : ContractPrimitiveBase<RescuePrimitive.Spec>
    {
        public override string Kind => "rescue";

        public struct Spec { public string Body; public double Sma; }

        protected override Spec Parse(JObject spec)
        {
            string body = (string)spec["body"];
            if (string.IsNullOrEmpty(body))
                throw new FormatException("rescue primitive missing 'body'");
            double? sma = spec.Value<double?>("sma");
            if (sma == null || sma.Value <= 0.0)
                throw new FormatException("rescue primitive missing 'sma'");
            return new Spec { Body = body, Sma = sma.Value };
        }

        protected override ContractParameter BuildFrom(Spec p)
            => new RescueKerbalParameter(p.Body, p.Sma);
    }
}
