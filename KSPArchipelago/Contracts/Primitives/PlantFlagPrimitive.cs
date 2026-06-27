using System;
using Contracts;
using Contracts.Parameters;
using Newtonsoft.Json.Linq;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind": "plant_flag", "body": "Mun" }</c>
    ///
    /// Maps to stock <see cref="PlantFlag"/>: completes when the player plants a
    /// flag on <c>body</c> (which implies a crewed surface EVA there). Self-tracks
    /// via GameEvents.onFlagPlant; no host contract entity required.
    /// </summary>
    public sealed class PlantFlagPrimitive : ContractPrimitiveBase<string>
    {
        public override string Kind => "plant_flag";

        protected override string Parse(JObject spec)
        {
            string bodyName = (string)spec["body"];
            if (string.IsNullOrEmpty(bodyName))
                throw new FormatException("plant_flag primitive missing 'body'");
            return bodyName;
        }

        protected override ContractParameter BuildFrom(string bodyName)
        {
            CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
            if (body == null)
                throw new FormatException($"plant_flag primitive: unknown body '{bodyName}'");
            return new PlantFlag(body);
        }
    }
}
