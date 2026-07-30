using System;
using Contracts;
using Contracts.Parameters;
using Newtonsoft.Json.Linq;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind": "sample_return", "body": "Mun" }</c>
    /// 
    /// Maps to a recovered-surface-sample requirement for the specified body.
    /// Implemented by RecoveredSurfaceSampleParameter, which mirrors MissionTracker's surface-sample detection.
    /// No host entity is required.
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
            return new KSPArchipelago.Contracts.Parameters.RecoveredSurfaceSampleParameter(bodyName);
        }
    }
}