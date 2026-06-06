using System.Collections.Generic;
using Contracts.Parameters;
using UnityEngine;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// Generic satellite — orbit a body. V1 treats polar/geosync as plain
    /// orbit (the envelope can still authorise those kinds; the contract
    /// just doesn't enforce the inclination/altitude specifics). Adding
    /// real polar/geosync constraints would need a custom OrbitParameter
    /// — deferred until we have a seed that needs it.
    /// </summary>
    public class ApProceduralSatelliteContract : ApProceduralContract
    {
        protected override string ContractTypeKey => "satellite";
        protected override string TypeDisplayName => "Satellite";

        private static readonly string[] _kinds = { "orbit", "polar", "geosync" };
        protected override IEnumerable<string> AllowedKinds => _kinds;

        protected override float RollPayloadKg(ContractEnvelope.KindEntry entry) => 0f;
        protected override float RollFunds(ContractEnvelope.KindEntry entry) => 12000f;

        protected override void PopulateParameters(
            string bodyName, string kind, ContractEnvelope.KindEntry envelope)
        {
            CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
            if (body == null) return;
            // V1: orbit-only success condition. polar/geosync flagged as
            // TODO — server can still authorise them via envelope but the
            // success check is the same as plain orbit until we add a
            // proper specific-orbit parameter.
            AddParameter(new EnterOrbit(body));
            if (kind == "polar" || kind == "geosync")
            {
                Debug.Log($"[KSP-AP] ApProceduralSatelliteContract: kind='{kind}' "
                        + $"treated as 'orbit' for v1 (specific-orbit parameter TODO)");
            }
        }
    }
}
