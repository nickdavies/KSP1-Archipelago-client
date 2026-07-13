using System;
using Contracts;
using Newtonsoft.Json.Linq;
using KSPArchipelago.Contracts.Parameters;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind": "surface_rescue", "body": "Mun", "lat": 35.3, "seed": 35318176 }</c>
    ///
    /// Builds a <see cref="SurfaceRescueKerbalParameter"/>, which SPAWNS a
    /// stranded Kerbal standing ON THE SURFACE of the body (stock RecoverAsset
    /// SURFACE-recovery style) and completes when they are recovered. Like
    /// <c>rescue</c>, this creates world state; the free-seat requirement rides
    /// as a separate has_any_part objective emitted by the generator.
    ///
    /// <c>lat</c> is the seeded site-latitude BOUND in degrees: the client
    /// places the Kerbal anywhere with |latitude| &lt;= lat (longitude free,
    /// water excluded) — the generator charges the ascent plane change for
    /// exactly this bound, so any placeable site costs no more than logic
    /// modelled. <c>seed</c> drives the deterministic site pick. Both required —
    /// missing/invalid values are rejected as schema drift.
    /// </summary>
    public sealed class SurfaceRescuePrimitive : ContractPrimitiveBase<SurfaceRescuePrimitive.Spec>
    {
        public override string Kind => "surface_rescue";

        public struct Spec { public string Body; public double Lat; public int Seed; }

        protected override Spec Parse(JObject spec)
        {
            string body = (string)spec["body"];
            if (string.IsNullOrEmpty(body))
                throw new FormatException("surface_rescue primitive missing 'body'");
            double? lat = spec.Value<double?>("lat");
            if (lat == null || lat.Value <= 0.0 || lat.Value > 90.0)
                throw new FormatException("surface_rescue primitive missing/invalid 'lat'");
            long? seed = spec.Value<long?>("seed");
            if (seed == null)
                throw new FormatException("surface_rescue primitive missing 'seed'");
            return new Spec { Body = body, Lat = lat.Value, Seed = unchecked((int)seed.Value) };
        }

        protected override ContractParameter BuildFrom(Spec p)
            => new SurfaceRescueKerbalParameter(p.Body, p.Lat, p.Seed);
    }
}
