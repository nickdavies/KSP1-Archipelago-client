using System;
using Contracts;
using UnityEngine;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// Base class for AP contracts bound 1:1 to a server-emitted
    /// ContractSlotSpec. Each instance "claims" a slot from
    /// ApContractSlotManager at Generate time and "consumes" it on
    /// completion by reporting the slot's ap_location_name.
    ///
    /// Anchored types currently supported:
    ///   - ApTestPartContract        (contract_type = "test_part")
    ///   - ApSatelliteWithPartContract (contract_type = "satellite_with_part")
    ///
    /// Anchored contracts read the active ContractEnvelope for sizing
    /// information (max_payload_kg, etc.) but the server picks the
    /// (body, kind, part) tuple verbatim from the slot spec.
    /// </summary>
    public abstract class ApAnchoredContract : ApContract
    {
        /// <summary>Subclass returns its slot_data contract_type string.</summary>
        protected abstract string ContractTypeKey { get; }

        // Set in Generate; persisted via OnSave/OnLoad. The slot itself is
        // looked up on each access so a slot_data refresh doesn't leave
        // contracts with stale params.
        public string SlotApLocationName { get; private set; }

        protected ContractSlotSpec Slot
            => ApContractSlotManager.FindSlot(SlotApLocationName);

        protected sealed override bool GeneratePopulate()
        {
            ContractSlotSpec slot = ApContractSlotManager.FindFreeEligibleSlot(ContractTypeKey);
            if (slot == null) return false;

            SlotApLocationName = slot.ApLocationName;

            if (slot.PaysFunds && slot.FundsReward > 0f)
            {
                SetFunds(advance: 0f, completion: slot.FundsReward, failure: 0f);
            }

            PopulateParameters(slot);
            return true;
        }

        /// <summary>
        /// Build the KSP ContractParameter tree from the slot spec. May
        /// throw FormatException on malformed params — caller logs and
        /// converts to "no contract this cycle".
        /// </summary>
        protected abstract void PopulateParameters(ContractSlotSpec slot);

        public override bool MeetRequirements()
            => ApContractSlotManager.HasFreeEligibleSlot(ContractTypeKey);

        protected override string GetHashString()
            => GetType().Name + "|" + (SlotApLocationName ?? "<no-slot>");

        protected override string GetTitle()
            => Slot?.Title ?? SlotApLocationName ?? GetType().Name;

        protected override string GetSynopsys()
            => Slot?.Synopsis ?? GetTitle();

        protected override string GetDescription()
            => Slot?.Synopsis ?? GetTitle();

        protected override void OnLoad(ConfigNode node)
        {
            SlotApLocationName = node.GetValue("ap_location_name");
            if (string.IsNullOrEmpty(SlotApLocationName))
            {
                Debug.LogWarning($"[KSP-AP] {GetType().Name} loaded with empty "
                               + "SlotApLocationName — will not report on complete.");
            }
        }

        protected override void OnSave(ConfigNode node)
        {
            if (!string.IsNullOrEmpty(SlotApLocationName))
                node.AddValue("ap_location_name", SlotApLocationName);
        }

        protected override void OnCompleted()
        {
            base.OnCompleted();
            ReportAndLog(SlotApLocationName);
        }
    }
}
