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
    public sealed class SpecificOrbitPrimitive : IContractPrimitive
    {
        public string Kind => "specific_orbit";

        public ContractParameter Build(JObject spec)
        {
            string bodyName = (string)spec["body"];
            if (string.IsNullOrEmpty(bodyName))
                throw new FormatException("specific_orbit primitive missing 'body'");
            CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
            if (body == null)
                throw new FormatException($"specific_orbit primitive: unknown body '{bodyName}'");

            OrbitType orbitType = ParseOrbitType((string)spec["orbit_type"]);
            double inclination = spec.Value<double>("inclination");
            double eccentricity = spec.Value<double>("eccentricity");
            double sma = spec.Value<double>("sma");
            double deviation = spec.Value<double?>("deviation") ?? 10.0;

            // ctor: (orbitType, inclination, eccentricity, sma, lan,
            //        argumentOfPeriapsis, meanAnomalyAtEpoch, epoch, body, deviationWindow)
            return new SpecificOrbitParameter(
                orbitType, inclination, eccentricity, sma,
                0.0, 0.0, 0.0, 0.0, body, deviation);
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
