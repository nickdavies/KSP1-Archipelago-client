using System;
using System.Collections.Generic;
using Contracts;
using UnityEngine;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// Owns the server-emitted contract manifest (item→spec, location→spec)
    /// parsed from slot_data["contracts"], and decides which contracts are
    /// offered. There is no eligibility "dance": a contract is offerable iff
    /// its AP item has been received this session, its location isn't already
    /// checked, and no live ApGenericContract is already bound to it.
    ///
    /// Receipt only marks the item (<see cref="MarkReceived"/>); offering and
    /// accepting happen later from a STABLE scene, never mid scene-load (adding
    /// a contract while ContractSystem is still loading races its own load and
    /// the contract is clobbered). Two cooperating paths bring contracts to
    /// ACTIVE, sharing one dedup:
    ///   - KSP's stock generation poll offers them (it runs inside
    ///     ContractSystem.Update, always post-load) via
    ///     ApGenericContract.MeetRequirements / GeneratePopulate.
    ///   - <see cref="ReconcileOffers"/> (driven from Update once the scene is
    ///     ready) accepts anything merely offered and force-activates anything
    ///     not yet present, via <see cref="TryForceOffer"/> — which mirrors
    ///     KSP's own GenerateContracts (Generate → Offer → Add → Accept).
    ///
    /// Sources of truth, deliberately not duplicated: the AP session
    /// (AllItemsReceived, replayed through GiveItem each connect) says what's
    /// received; the live KSP contract is the claim record; MissionTracker
    /// says what's checked. We keep no separate persisted offer list.
    /// </summary>
    public static class ApContractManager
    {
        private static readonly List<ContractSlotSpec> _specs = new List<ContractSlotSpec>();
        private static readonly Dictionary<string, ContractSlotSpec> _byItem
            = new Dictionary<string, ContractSlotSpec>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ContractSlotSpec> _byLocation
            = new Dictionary<string, ContractSlotSpec>(StringComparer.Ordinal);

        // Contract items received this session. Rebuilt from scratch each
        // connect: SetSpecs clears it, then GiveItem (re-walking
        // AllItemsReceived) re-marks via MarkReceived.
        private static readonly HashSet<string> _receivedItems
            = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Replace the manifest from slot_data (called on AP connect). Clears
        /// the received set — it is rebuilt by the GiveItem replay that follows.
        /// </summary>
        public static void SetSpecs(IEnumerable<ContractSlotSpec> specs)
        {
            _specs.Clear();
            _byItem.Clear();
            _byLocation.Clear();
            _receivedItems.Clear();
            if (specs != null)
            {
                foreach (var s in specs)
                {
                    _specs.Add(s);
                    _byItem[s.Item] = s;
                    // Map EVERY reward slot to the spec so GetByLocation resolves
                    // a contract loaded/bound by either slot name.
                    foreach (var loc in s.Locations)
                        _byLocation[loc] = s;
                }
            }
            Debug.Log($"[KSP-AP] ApContractManager: loaded {_specs.Count} contract(s)");
        }

        public static int SpecCount => _specs.Count;

        public static bool IsContractItem(string itemName)
            => itemName != null && _byItem.ContainsKey(itemName);

        public static ContractSlotSpec GetByLocation(string location)
            => location != null && _byLocation.TryGetValue(location, out var s) ? s : null;

        /// <summary>
        /// Called from KSPArchipelagoPartsManager.GiveItem when a contract item
        /// arrives. Records it as received — actual offering/accepting happens
        /// later from a stable scene (ReconcileOffers / KSP's stock poll), never
        /// here, because GiveItem can run mid scene-load where adding a contract
        /// races ContractSystem's own load. No-op for non-contract items.
        /// </summary>
        public static bool MarkReceived(string itemName)
        {
            if (!_byItem.ContainsKey(itemName)) return false;
            _receivedItems.Add(itemName);
            return true;
        }

        /// <summary>First received-but-not-offered, not-checked contract, or null.</summary>
        public static ContractSlotSpec FindNextOfferable()
        {
            HashSet<string> live = SnapshotLiveLocations();
            foreach (ContractSlotSpec spec in _specs)
            {
                if (!_receivedItems.Contains(spec.Item)) continue;
                if (IsApLocationChecked(spec.Location)) continue;
                if (live.Contains(spec.Location)) continue;
                return spec;
            }
            return null;
        }

        /// <summary>True iff at least one received contract is not yet live.</summary>
        /// <summary>
        /// Force a single contract straight to ACTIVE, using KSP's own
        /// generate→offer→add→accept sequence ("if the server says you have it,
        /// you always get it" — and contract parameters only track on active
        /// contracts). Safe no-op when it can't run now (no ContractSystem,
        /// already checked, or already live). Returns true if it went active.
        /// </summary>
        public static bool TryForceOffer(ContractSlotSpec spec)
        {
            if (spec == null) return false;
            // ContractSystem.Instance is null outside Career / in the editor.
            if (ContractSystem.Instance == null) return false;
            if (IsApLocationChecked(spec.Location)) return false;
            if (SnapshotLiveLocations().Contains(spec.Location)) return false;

            try
            {
                ApGenericContract.PendingSpec = spec;
                int seed = Math.Abs(spec.Location.GetHashCode());
                Contract c = Contract.Generate(
                    typeof(ApGenericContract),
                    Contract.ContractPrestige.Trivial,
                    seed,
                    Contract.State.Generated);
                if (c == null)
                {
                    Debug.LogWarning($"[KSP-AP] force-offer: Generate returned null for '{spec.Location}'");
                    return false;
                }
                c.Offer();
                ContractSystem.Instance.Contracts.Add(c);
                // Auto-accept so the contract is ACTIVE and its parameters start
                // tracking immediately — the player never has to click Accept.
                bool accepted = c.Accept();
                Debug.Log($"[KSP-AP] force-offer '{spec.Location}': added, "
                        + $"accept={accepted}, state={c.ContractState}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KSP-AP] force-offer failed for '{spec.Location}': {ex}");
                return false;
            }
            finally
            {
                ApGenericContract.PendingSpec = null;
            }
        }

        /// <summary>
        /// Bring every owed contract to ACTIVE. Called on entering a scene with
        /// a live ContractSystem. Three passes:
        ///   1. Remove duplicate live contracts for the same location (safety
        ///      net / heals saves from the old stock-poll duplication bug).
        ///   2. Accept any ApGenericContract still sitting in Offered.
        ///   3. Force-activate any received contract not yet present.
        /// </summary>
        /// <summary>
        /// True once KSP's ContractSystem exists and its contract list is loaded.
        /// The mod uses this as a self-healing fallback for the reconcile gate when
        /// the (intermittent) onContractsLoaded event doesn't fire for a scene.
        /// </summary>
        public static bool ContractsReady()
            => ContractSystem.Instance?.Contracts != null;

        public static void ReconcileOffers()
        {
            var cs = ContractSystem.Instance;
            if (cs?.Contracts == null) return;

            // Pass 1: drop duplicate live contracts (same BoundLocation), keep one.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Contract c in new List<Contract>(cs.Contracts))
            {
                if (!(c is ApGenericContract g) || string.IsNullOrEmpty(g.BoundLocation)) continue;
                if (c.ContractState != Contract.State.Offered
                    && c.ContractState != Contract.State.Active) continue;
                if (seen.Add(g.BoundLocation)) continue;   // first one — keep
                try { c.Unregister(); } catch { }
                cs.Contracts.Remove(c);
                Debug.Log($"[KSP-AP] removed duplicate contract '{g.BoundLocation}'");
            }

            // Pass 2: accept anything still merely offered.
            foreach (Contract c in new List<Contract>(cs.Contracts))
            {
                if (c is ApGenericContract && c.ContractState == Contract.State.Offered)
                    c.Accept();
            }

            ContractSlotSpec spec;
            // Each TryForceOffer adds one active contract, so the next
            // FindNextOfferable skips it — this terminates at the manifest size.
            while ((spec = FindNextOfferable()) != null)
            {
                if (!TryForceOffer(spec)) break;
            }

            // Pass 4: heal a partially-reported multi-slot contract. OnCompleted
            // reports every slot, but a crash between the two sends could leave a
            // sibling slot unchecked while the primary is checked. ReportLocation
            // is idempotent, so re-firing the missing slot is safe.
            foreach (ContractSlotSpec s in _specs)
            {
                if (s.Locations.Count < 2) continue;
                if (!IsApLocationChecked(s.Location)) continue;
                foreach (var loc in s.Locations)
                    if (!IsApLocationChecked(loc)) ReportApLocation(loc);
            }
        }

        private static HashSet<string> SnapshotLiveLocations()
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            var cs = ContractSystem.Instance;
            if (cs?.Contracts == null) return result;
            foreach (Contract c in cs.Contracts)
            {
                if (c is ApGenericContract g
                    && !string.IsNullOrEmpty(g.BoundLocation)
                    && (c.ContractState == Contract.State.Offered
                        || c.ContractState == Contract.State.Active))
                {
                    result.Add(g.BoundLocation);
                }
            }
            return result;
        }

        private static bool IsApLocationChecked(string apLocationName)
        {
            if (string.IsNullOrEmpty(apLocationName)) return false;
            var mod = UnityEngine.Object.FindObjectOfType<KSPArchipelagoMod>();
            var tracker = mod?.Tracker;
            return tracker != null && tracker.IsLocationChecked(apLocationName);
        }

        private static void ReportApLocation(string apLocationName)
        {
            if (string.IsNullOrEmpty(apLocationName)) return;
            var mod = UnityEngine.Object.FindObjectOfType<KSPArchipelagoMod>();
            mod?.Tracker?.ReportLocation(apLocationName);
        }
    }
}
