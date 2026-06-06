using System;
using System.Collections.Generic;
using Contracts;
using UnityEngine;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// Owner of the live ContractSlotSpec pool. Loaded from slot_data
    /// at AP connect time (KSPArchipelagoMod.HandleConnect → SetSlots).
    /// ApContract subclasses query this to find an offerable slot of
    /// their contract_type, claim it through the contract lifecycle,
    /// and consume it on completion.
    ///
    /// Three slot states:
    ///   - Consumed: the AP location has been checked. Never re-offer.
    ///     Source of truth: MissionTracker.checkedLocationIds → if a slot's
    ///     ApLocationName is checked, it's consumed. We don't track this
    ///     in-class so save/load doesn't need to worry about it.
    ///   - Claimed: an ApContract instance currently references this slot
    ///     (offered or active). Source of truth: walk ContractSystem and
    ///     look at ApContract.SlotApLocationName. Re-derives at load.
    ///   - Free: not consumed, not claimed. Available for Generate().
    ///
    /// Why no in-class claim tracking? KSP serialises Contract instances
    /// via OnLoad/OnSave; the contract IS the claim record. Adding a
    /// separate claim list creates two sources of truth that drift.
    /// </summary>
    public static class ApContractSlotManager
    {
        private static readonly List<ContractSlotSpec> _slots = new List<ContractSlotSpec>();
        private static readonly Dictionary<string, ContractSlotSpec> _byApName
            = new Dictionary<string, ContractSlotSpec>();

        /// <summary>
        /// Replace the slot pool. Called from KSPArchipelagoMod after
        /// slot_data parsing on AP connect. Idempotent — calling with
        /// the same list is safe; live contracts that reference now-removed
        /// slots will get null from FindSlot and should gracefully fail.
        /// </summary>
        public static void SetSlots(IEnumerable<ContractSlotSpec> slots)
        {
            _slots.Clear();
            _byApName.Clear();
            if (slots == null) return;
            foreach (var s in slots)
            {
                _slots.Add(s);
                _byApName[s.ApLocationName] = s;
            }
            Debug.Log($"[KSP-AP] ApContractSlotManager: loaded {_slots.Count} slots");
        }

        public static int SlotCount => _slots.Count;

        public static ContractSlotSpec FindSlot(string apLocationName)
        {
            if (apLocationName == null) return null;
            return _byApName.TryGetValue(apLocationName, out var s) ? s : null;
        }

        /// <summary>
        /// Return the first slot of the given contract_type that is
        /// neither consumed nor claimed and whose Requires expression
        /// evaluates true against currently-checked AP locations.
        /// Returns null if no such slot exists.
        /// </summary>
        public static ContractSlotSpec FindFreeEligibleSlot(string contractType)
        {
            // Pre-compute the claim set once. Walking ContractSystem per slot
            // is O(slots * contracts) which is fine for current scale (~50
            // slots) but cheap to optimise if it ever matters.
            HashSet<string> claimed = SnapshotClaimedSlotNames();
            Func<string, bool> isChecked = IsApLocationChecked;

            foreach (var s in _slots)
            {
                if (s.ContractType != contractType) continue;
                if (isChecked(s.ApLocationName)) continue;     // consumed
                if (claimed.Contains(s.ApLocationName)) continue; // already offered/active
                if (!s.Requires.IsSatisfied(isChecked)) continue;
                return s;
            }
            return null;
        }

        /// <summary>True iff at least one slot of this contract_type is offerable RIGHT NOW.</summary>
        public static bool HasFreeEligibleSlot(string contractType)
            => FindFreeEligibleSlot(contractType) != null;

        /// <summary>
        /// Build the set of slot AP location names currently referenced by
        /// an offered/active ApContract. Excludes Completed/Withdrawn/
        /// Declined/Failed contracts — those slots are eligible for
        /// re-offer (until consumed via the AP check).
        /// </summary>
        private static HashSet<string> SnapshotClaimedSlotNames()
        {
            var result = new HashSet<string>();
            var cs = ContractSystem.Instance;
            if (cs == null) return result;

            CollectFromList(cs.Contracts, result);
            // Offers are a separate list on ContractSystem. Field name
            // verified from notes/ksp1_contracts_system.md; cast through
            // reflection because the field type may differ across KSP
            // versions and we don't want a brittle direct reference.
            try
            {
                var offers = cs.GetType().GetField("Offers")?.GetValue(cs)
                             as System.Collections.IEnumerable;
                CollectFromList(offers, result);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KSP-AP] SnapshotClaimedSlotNames offers lookup failed: {ex.Message}");
            }
            return result;
        }

        private static void CollectFromList(System.Collections.IEnumerable list,
                                            HashSet<string> sink)
        {
            if (list == null) return;
            foreach (object o in list)
            {
                if (o is ApAnchoredContract ap
                    && !string.IsNullOrEmpty(ap.SlotApLocationName))
                    sink.Add(ap.SlotApLocationName);
            }
        }

        /// <summary>
        /// True iff the named AP location has already been reported as
        /// checked. Reads from MissionTracker via the mod singleton.
        /// </summary>
        public static bool IsApLocationChecked(string apLocationName)
        {
            if (string.IsNullOrEmpty(apLocationName)) return false;
            var mod = UnityEngine.Object.FindObjectOfType<KSPArchipelagoMod>();
            var tracker = mod?.Tracker;
            return tracker != null && tracker.IsLocationChecked(apLocationName);
        }
    }
}
