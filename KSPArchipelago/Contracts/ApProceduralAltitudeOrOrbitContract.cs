using System.Collections.Generic;
using Contracts.Parameters;
using UnityEngine;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// "Safety net" procedural type — reach altitude OR get to orbit.
    /// Cheap, always-doable when the envelope is non-empty for these kinds,
    /// pays a small funds amount so a stuck player can dig out.
    /// </summary>
    public class ApProceduralAltitudeOrOrbitContract : ApProceduralContract
    {
        protected override string ContractTypeKey => "altitude_or_orbit";
        protected override string TypeDisplayName => "Altitude / Orbit";

        private static readonly string[] _kinds = { "altitude", "orbit" };
        protected override IEnumerable<string> AllowedKinds => _kinds;

        // Always payload=0 — this is the "do basic thing" tier.
        protected override float RollPayloadKg(ContractEnvelope.KindEntry entry) => 0f;
        protected override float RollFunds(ContractEnvelope.KindEntry entry) => 5000f;

        protected override void PopulateParameters(
            string bodyName, string kind, ContractEnvelope.KindEntry envelope)
        {
            CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
            if (body == null) return;
            if (kind == "altitude")
            {
                float maxAlt = envelope.MaxAltitudeM > 0 ? envelope.MaxAltitudeM : 70000f;
                // Random target altitude in [25%, 100%] of envelope max so
                // procedural variety is meaningful but always reachable.
                float target = Random.Range(maxAlt * 0.25f, maxAlt);
                AddParameter(new ReachDestination(body, "Reach " + body.bodyName));
                AddParameter(new ReachAltitudeEnvelope(target, float.MaxValue));
            }
            else
            {
                AddParameter(new EnterOrbit(body));
            }
        }
    }
}
