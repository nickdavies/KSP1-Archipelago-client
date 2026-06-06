using System.Collections.Generic;
using Contracts.Parameters;
using KSPArchipelago.Contracts.Parameters;
using UnityEngine;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// Deliver a dead-mass payload to a destination. Pays well — this is
    /// the procedural type that scales with envelope's max_payload_kg
    /// to give players a funds outlet that grows with capability.
    /// </summary>
    public class ApProceduralMassDeliveryContract : ApProceduralContract
    {
        protected override string ContractTypeKey => "mass_delivery";
        protected override string TypeDisplayName => "Mass Delivery";

        private static readonly string[] _kinds = {
            "orbit", "polar", "geosync", "flyby", "landing" };
        protected override IEnumerable<string> AllowedKinds => _kinds;

        protected override float RollPayloadKg(ContractEnvelope.KindEntry entry)
        {
            // At least 50 kg or 25% of max, whichever is greater. Otherwise
            // procedural mass-delivery contracts are trivial when envelope
            // max is high.
            float floor = Mathf.Max(50f, entry.MaxPayloadKg * 0.25f);
            return Random.Range(floor, Mathf.Max(floor, entry.MaxPayloadKg));
        }

        protected override float RollFunds(ContractEnvelope.KindEntry entry)
            => 8000f + 50f * ChosenPayloadKg;

        protected override void PopulateParameters(
            string bodyName, string kind, ContractEnvelope.KindEntry envelope)
        {
            CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
            if (body == null) return;
            switch (kind)
            {
                case "orbit":
                case "polar":
                case "geosync":
                    AddParameter(new EnterOrbit(body));
                    break;
                case "flyby":
                    AddParameter(new EnterSOI(body));
                    break;
                case "landing":
                    AddParameter(new LandOnBody(body));
                    break;
            }
            // MinVesselMassParameter operates in tonnes; payload is in kg.
            AddParameter(new MinVesselMassParameter(ChosenPayloadKg / 1000f));
        }

        protected override string GetTitle()
            => $"Mass Delivery — {ChosenPayloadKg:F0} kg to {ChosenBody} {ChosenKind}";
    }
}
