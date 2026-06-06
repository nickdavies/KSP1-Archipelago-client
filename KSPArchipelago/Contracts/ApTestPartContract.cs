using System;
using Contracts.Parameters;
using UnityEngine;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// Anchored "test part X at body/situation" contract. Uses stock
    /// PartTest parameter which handles the test-button mechanic and
    /// situation gating natively.
    ///
    /// Expected slot params:
    /// <code>
    /// {
    ///   "part_name":        "solidBooster",
    ///   "destination_body": "Kerbin",
    ///   "destination_kind": "suborbital"
    /// }
    /// </code>
    /// The envelope (body, kind) entry MUST exist; if missing the
    /// contract still spawns (server is the source of truth on what's
    /// reachable) but a warning is logged for diagnostic visibility.
    /// </summary>
    public class ApTestPartContract : ApAnchoredContract
    {
        protected override string ContractTypeKey => "test_part";

        protected override void PopulateParameters(ContractSlotSpec slot)
        {
            string partName = (string)slot.Params["part_name"];
            string bodyName = (string)slot.Params["destination_body"];
            string kind     = (string)slot.Params["destination_kind"];
            if (string.IsNullOrEmpty(partName))
                throw new FormatException("test_part slot missing 'part_name'");
            if (string.IsNullOrEmpty(bodyName))
                throw new FormatException("test_part slot missing 'destination_body'");
            if (string.IsNullOrEmpty(kind))
                throw new FormatException("test_part slot missing 'destination_kind'");

            CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
            if (body == null)
                throw new FormatException(
                    $"test_part: unknown body '{bodyName}'");

            AvailablePart part = PartLoader.getPartInfoByName(partName);
            if (part == null)
                throw new FormatException(
                    $"test_part: unknown part '{partName}'");

            Vessel.Situations situation = KindToSituation(kind);

            if (!ApContractsRuntime.Envelope.Has(bodyName, kind))
            {
                Debug.LogWarning(
                    $"[KSP-AP] ApTestPartContract: anchored slot "
                    + $"'{slot.ApLocationName}' targets ({bodyName},{kind}) "
                    + "which is not in envelope. Spawning anyway — server is "
                    + "source of truth on reachability.");
            }

            // PartTest(part, notes, repeat, body, situation, id, hauled).
            // hauled=false means the part must actually be activated (not
            // just brought along).
            AddParameter(new PartTest(
                tgtPart: part,
                testNotes: "",
                partRepeatability: PartTestConstraint.TestRepeatability.ONCEPERPART,
                tgtBody: body,
                tgtSit: situation,
                id: slot.ApLocationName,
                hauled: false));
        }

        internal static Vessel.Situations KindToSituation(string kind)
        {
            switch (kind)
            {
                case "altitude":    return Vessel.Situations.FLYING;
                case "suborbital":  return Vessel.Situations.SUB_ORBITAL;
                case "orbit":
                case "polar":
                case "geosync":     return Vessel.Situations.ORBITING;
                case "flyby":       return Vessel.Situations.ESCAPING;
                case "landing":     return Vessel.Situations.LANDED;
                default:            return Vessel.Situations.FLYING;
            }
        }
    }
}
