using System;
using System.Collections.Generic;
using System.Text;
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
                    _byLocation[s.Location] = s;
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

            LogStateSummaryOnChange(cs);
        }

        // Diagnostic: how many of our contracts are live, by state. Logged only
        // when it changes, so a steady state is silent. Reveals whether a
        // force-offered contract actually persists in ContractSystem (vs. being
        // clobbered by KSP's own contract load).
        private static string _lastSummary;

        private static void LogStateSummaryOnChange(ContractSystem cs)
        {
            int active = 0, offered = 0, other = 0;
            foreach (Contract c in cs.Contracts)
            {
                if (!(c is ApGenericContract)) continue;
                if (c.ContractState == Contract.State.Active) active++;
                else if (c.ContractState == Contract.State.Offered) offered++;
                else other++;
            }
            string summary = $"AP contracts in ContractSystem: {active} active, "
                + $"{offered} offered, {other} other (received={_receivedItems.Count}, "
                + $"manifest={_specs.Count})";
            if (summary != _lastSummary)
            {
                Debug.Log($"[KSP-AP] {summary}");
                _lastSummary = summary;
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

        // ------------------------------------------------------------------
        // Telemetry — logged on change so a steady state is silent. Lets us
        // diagnose param non-completion in ONE play-test instead of guessing:
        // it shows whether each contract is Active, whether its parameters
        // exist and flip Complete, and the live vessel/resource state the stock
        // params are evaluating against.
        // ------------------------------------------------------------------
        private static string _lastTelem;

        public static void LogTelemetryOnChange()
        {
            try
            {
                var sb = new StringBuilder();
                var cs = ContractSystem.Instance;
                int apCount = 0;
                if (cs?.Contracts != null)
                {
                    foreach (Contract c in cs.Contracts)
                    {
                        if (!(c is ApGenericContract g)) continue;
                        apCount++;
                        sb.Append($" [{g.BoundLocation}|{c.ContractState}");
                        foreach (ContractParameter p in c.AllParameters)
                            sb.Append($" {DescribeParam(p)}");
                        sb.Append("]");
                    }
                }

                Vessel v = FlightGlobals.ActiveVessel;
                string vctx;
                if (v == null) vctx = "vessel=none";
                else
                {
                    double ore = 0;
                    int crewCap = 0;
                    if (v.parts != null)
                        foreach (Part part in v.parts)
                        {
                            crewCap += part.CrewCapacity;
                            if (part.Resources != null)
                                foreach (PartResource r in part.Resources)
                                    if (r.resourceName == "Ore") ore += r.amount;
                        }
                    vctx = $"vessel='{v.vesselName}' body={v.mainBody?.name} "
                         + $"sit={v.situation} type={v.vesselType} ore={ore:F0} "
                         + $"crewCap={crewCap}";
                }

                string summary = $"AP-TELEM contracts={apCount}{sb} | {vctx}";
                if (summary != _lastTelem)
                {
                    Debug.Log($"[KSP-AP] {summary}");
                    _lastTelem = summary;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KSP-AP] telemetry failed: {ex.Message}");
            }
        }

        // Reflect the stock params' own target fields + last-computed values so
        // the log distinguishes: OnUpdate-not-running (currentResource stays 0
        // while the vessel has ore), data mismatch (targetBody != where you
        // are), or a completion-logic bug (targets match, value met, still
        // Incomplete). The fields are private — read by reflection, cached.
        private static System.Reflection.FieldInfo _fSitBody, _fSitSituation;
        private static System.Reflection.FieldInfo _fResName, _fResGoal, _fResCurrent;

        private static string DescribeParam(ContractParameter p)
        {
            string name = p.GetType().Name;
            try
            {
                if (name == "LocationAndSituationParameter")
                {
                    var t = p.GetType();
                    if (_fSitBody == null) _fSitBody = Priv(t, "targetBody");
                    if (_fSitSituation == null) _fSitSituation = Priv(t, "targetSituation");
                    var body = _fSitBody?.GetValue(p) as CelestialBody;
                    object sit = _fSitSituation?.GetValue(p);
                    return $"Situation={p.State}(body={body?.name},sit={sit})";
                }
                if (name == "ResourcePossessionParameter")
                {
                    var t = p.GetType();
                    if (_fResName == null) _fResName = Priv(t, "resourceName");
                    if (_fResGoal == null) _fResGoal = Priv(t, "goalResource");
                    if (_fResCurrent == null) _fResCurrent = Priv(t, "currentResource");
                    double goal = Convert.ToDouble(_fResGoal?.GetValue(p) ?? 0.0);
                    double cur = Convert.ToDouble(_fResCurrent?.GetValue(p) ?? 0.0);
                    return $"Resource={p.State}(res={_fResName?.GetValue(p)},"
                         + $"goal={goal:F0},cur={cur:F0})";
                }
            }
            catch { /* fall through to the plain form */ }
            return $"{name}={p.State}";
        }

        private static System.Reflection.FieldInfo Priv(Type t, string field)
            => t.GetField(field, System.Reflection.BindingFlags.NonPublic
                               | System.Reflection.BindingFlags.Public
                               | System.Reflection.BindingFlags.Instance);
    }
}
