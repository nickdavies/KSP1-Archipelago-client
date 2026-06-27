using System;
using Contracts;
using FinePrint.Contracts.Parameters;
using FinePrint.Utilities;
using Newtonsoft.Json.Linq;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind":"specific_orbit", "body":"Mun", "orbit_type":"EQUATORIAL",
    ///      "inclination":0, "eccentricity":0, "sma":214000, "deviation":10 }</c>
    ///
    /// Maps to stock <see cref="SpecificOrbitParameter"/> — the param the satellite
    /// contracts use. It renders the blue target orbit and completes when the
    /// active vessel matches within <c>deviation</c>. <c>lan</c>/<c>argPe</c>/
    /// <c>MNA</c>/<c>epoch</c> are 0 for the circular orbits the generator emits and
    /// are not sent over the wire.
    /// </summary>
    public sealed class SpecificOrbitPrimitive : ContractPrimitiveBase<SpecificOrbitPrimitive.Spec>
    {
        public override string Kind => "specific_orbit";

        public struct Spec
        {
            public string BodyName;
            public OrbitType OrbitType;
            public double Inclination, Eccentricity, Sma, Deviation;
        }

        protected override Spec Parse(JObject spec)
        {
            string bodyName = (string)spec["body"];
            if (string.IsNullOrEmpty(bodyName))
                throw new FormatException("specific_orbit primitive missing 'body'");
            double? sma = spec.Value<double?>("sma");
            if (sma == null || sma.Value <= 0.0)
                throw new FormatException("specific_orbit primitive missing 'sma'");
            return new Spec
            {
                BodyName = bodyName,
                OrbitType = ParseOrbitType((string)spec["orbit_type"]),  // throws on unknown
                Inclination = spec.Value<double>("inclination"),
                Eccentricity = spec.Value<double>("eccentricity"),
                Sma = sma.Value,
                Deviation = spec.Value<double?>("deviation") ?? 10.0,
            };
        }

        protected override ContractParameter BuildFrom(Spec p)
        {
            CelestialBody body = FlightGlobals.GetBodyByName(p.BodyName);
            if (body == null)
                throw new FormatException($"specific_orbit primitive: unknown body '{p.BodyName}'");

            // ctor: (orbitType, inclination, eccentricity, sma, lan,
            //        argumentOfPeriapsis, meanAnomalyAtEpoch, epoch, body, deviationWindow)
            return new SpecificOrbitParameter(
                p.OrbitType, p.Inclination, p.Eccentricity, p.Sma,
                0.0, 0.0, 0.0, 0.0, body, p.Deviation);
        }

        private static OrbitType ParseOrbitType(string s)
        {
            switch ((s ?? "").ToUpperInvariant())
            {
                case "EQUATORIAL":  return OrbitType.EQUATORIAL;
                case "POLAR":       return OrbitType.POLAR;
                case "STATIONARY":  return OrbitType.STATIONARY;
                case "SYNCHRONOUS": return OrbitType.SYNCHRONOUS;
                case "KOLNIYA":     return OrbitType.KOLNIYA;
                case "TUNDRA":      return OrbitType.TUNDRA;
                case "RANDOM":      return OrbitType.RANDOM;
                default:
                    throw new FormatException(
                        $"specific_orbit primitive: unknown orbit_type '{s}'");
            }
        }
    }
}
