using System;
using Contracts.Parameters;
using KSPArchipelago.Contracts.Parameters;
using UnityEngine;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// Anchored "satellite carrying part X in orbit around body" contract.
    /// Composes stock EnterOrbit + custom VesselHasPartParameter.
    /// V1 treats polar/geosync as plain orbit (specific-orbit param TODO).
    ///
    /// Expected slot params:
    /// <code>
    /// {
    ///   "required_part":    "Science Jr.",
    ///   "destination_body": "Mun",
    ///   "destination_kind": "orbit"
    /// }
    /// </code>
    /// </summary>
    public class ApSatelliteWithPartContract : ApAnchoredContract
    {
        protected override string ContractTypeKey => "satellite_with_part";

        protected override void PopulateParameters(ContractSlotSpec slot)
        {
            string partName = (string)slot.Params["required_part"];
            string bodyName = (string)slot.Params["destination_body"];
            string kind     = (string)slot.Params["destination_kind"];
            if (string.IsNullOrEmpty(partName))
                throw new FormatException("satellite_with_part slot missing 'required_part'");
            if (string.IsNullOrEmpty(bodyName))
                throw new FormatException("satellite_with_part slot missing 'destination_body'");
            if (string.IsNullOrEmpty(kind))
                throw new FormatException("satellite_with_part slot missing 'destination_kind'");

            CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
            if (body == null)
                throw new FormatException(
                    $"satellite_with_part: unknown body '{bodyName}'");

            if (kind != "orbit" && kind != "polar" && kind != "geosync")
                throw new FormatException(
                    $"satellite_with_part: destination_kind must be one of "
                    + $"orbit/polar/geosync, got '{kind}'");

            if (!ApContractsRuntime.Envelope.Has(bodyName, kind))
            {
                Debug.LogWarning(
                    $"[KSP-AP] ApSatelliteWithPartContract: anchored slot "
                    + $"'{slot.ApLocationName}' targets ({bodyName},{kind}) "
                    + "which is not in envelope. Spawning anyway.");
            }

            AddParameter(new EnterOrbit(body));
            AddParameter(new VesselHasPartParameter(partName));

            if (kind == "polar" || kind == "geosync")
            {
                Debug.Log($"[KSP-AP] ApSatelliteWithPartContract: kind='{kind}' "
                        + $"treated as plain 'orbit' for v1 (specific-orbit param TODO)");
            }
        }
    }
}
