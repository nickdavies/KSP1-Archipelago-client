using System;
using Contracts;
using FinePrint.Contracts.Parameters;
using Newtonsoft.Json.Linq;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind": "crew_capacity", "min": 5 }</c>
    ///
    /// Maps to stock <see cref="CrewCapacityParameter"/>: completes when the
    /// active vessel's crew CAPACITY (total seats, occupied or not) is at least
    /// <c>min</c>. Body/situation (e.g. in orbit around X) is a separate
    /// `situation` parameter — this one only counts seats. Self-tracks via
    /// onVesselWasModified / onPartDie; no host contract entity required.
    /// </summary>
    public sealed class CrewCapacityPrimitive : ContractPrimitiveBase<int>
    {
        public override string Kind => "crew_capacity";

        protected override int Parse(JObject spec)
        {
            JToken minTok = spec["min"];
            if (minTok == null)
                throw new FormatException("crew_capacity primitive missing 'min'");
            return (int)minTok;
        }

        protected override ContractParameter BuildFrom(int min)
            => new CrewCapacityParameter(min);
    }
}
