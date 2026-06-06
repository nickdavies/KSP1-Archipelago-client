using System.Collections.Generic;
using Contracts.Parameters;
using KSPArchipelago.Contracts.Parameters;
using UnityEngine;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// "Take N tourists to body/situation." V1 doesn't spawn dedicated
    /// tourist ProtoCrewMembers — it just gates on vessel crew count.
    /// Generation side enforces difficulty by capping the envelope.
    ///
    /// Tourist count is rolled 1..3; reward scales with count and kind.
    /// </summary>
    public class ApProceduralTouristContract : ApProceduralContract
    {
        protected override string ContractTypeKey => "tourist";
        protected override string TypeDisplayName => "Tourist";

        private static readonly string[] _kinds = {
            "suborbital", "orbit", "flyby", "landing" };
        protected override IEnumerable<string> AllowedKinds => _kinds;

        private int _touristCount = 1;

        protected override float RollPayloadKg(ContractEnvelope.KindEntry entry) => 0f;
        protected override float RollFunds(ContractEnvelope.KindEntry entry)
            => 8000f + 5000f * _touristCount;

        protected override void PopulateParameters(
            string bodyName, string kind, ContractEnvelope.KindEntry envelope)
        {
            CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
            if (body == null) return;
            _touristCount = Random.Range(1, 4);   // 1..3 inclusive

            AddParameter(new ReachDestination(body, "Take tourists to " + body.bodyName));
            AddParameter(new MinCrewCountParameter(_touristCount));
            switch (kind)
            {
                case "suborbital":
                    AddParameter(new ReachSituation(
                        Vessel.Situations.SUB_ORBITAL, "Reach sub-orbital trajectory"));
                    break;
                case "orbit":
                    AddParameter(new EnterOrbit(body));
                    break;
                case "flyby":
                    AddParameter(new EnterSOI(body));
                    break;
                case "landing":
                    AddParameter(new LandOnBody(body));
                    break;
            }
        }

        protected override string GetTitle()
            => $"Tourist — {_touristCount} to {ChosenBody} {ChosenKind}";
    }
}
