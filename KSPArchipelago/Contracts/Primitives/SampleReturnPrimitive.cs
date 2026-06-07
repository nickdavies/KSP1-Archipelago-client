using System;
using Contracts;
using Contracts.Parameters;
using Newtonsoft.Json.Linq;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind": "sample_return", "body": "Mun" }</c>
    ///
    /// Maps to stock <see cref="CollectScience"/> bound to the body's SURFACE:
    /// completes when surface science from <c>body</c> is received (recovered or
    /// transmitted) — the surface-sample-return mission satisfies it on recovery.
    /// Self-tracks via GameEvents.OnScienceRecieved; no host entity required.
    ///
    /// Note: stock CollectScience(Surface) fires on any surface science from the
    /// body, which is slightly looser than "a surface SAMPLE specifically" — an
    /// acceptable out-of-logic easing (the contract item + physics gates still
    /// apply). If strict sample-only is wanted later, a custom recover param
    /// mirroring MissionTracker's surfaceSample detection is the upgrade.
    /// </summary>
    public sealed class SampleReturnPrimitive : IContractPrimitive
    {
        public string Kind => "sample_return";

        public ContractParameter Build(JObject spec)
        {
            string bodyName = (string)spec["body"];
            if (string.IsNullOrEmpty(bodyName))
                throw new FormatException("sample_return primitive missing 'body'");
            CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
            if (body == null)
                throw new FormatException($"sample_return primitive: unknown body '{bodyName}'");
            return new CollectScience(body, BodyLocation.Surface);
        }
    }
}
