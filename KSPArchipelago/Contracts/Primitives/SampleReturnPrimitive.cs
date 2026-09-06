using System;
using Contracts;
using Newtonsoft.Json.Linq;
using KSPArchipelago.Contracts.Parameters;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind": "sample_return", "body": "Mun" }</c>
    ///
    /// Semantics: a <c>{body}</c> surface SAMPLE physically came home. No crew
    /// check — an uncrewed sample-return probe counts. Transmitted science never
    /// counts: transmitting leaves the sample where it was taken.
    ///
    /// Builds a <see cref="RecoveredSurfaceSampleParameter"/>, which rides the
    /// shared <c>MissionEvidence</c> milestone — the same evidence
    /// MissionTracker awards the <c>{B} Sample Return</c> AP location from, so
    /// the objective and its location cannot disagree.
    /// </summary>
    public sealed class SampleReturnPrimitive : ContractPrimitiveBase<string>
    {
        public override string Kind => "sample_return";

        protected override string Parse(JObject spec)
        {
            string bodyName = (string)spec["body"];
            if (string.IsNullOrEmpty(bodyName))
                throw new FormatException("sample_return primitive missing 'body'");
            return bodyName;
        }

        protected override ContractParameter BuildFrom(string bodyName)
        {
            CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
            if (body == null)
                throw new FormatException($"sample_return primitive: unknown body '{bodyName}'");
            return new RecoveredSurfaceSampleParameter(bodyName);
        }
    }
}
