using System;
using System.Collections.Generic;
using System.Linq;
using Contracts;
using UnityEngine;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// Removes every non-AP contract type from KSP's offer pool, so the
    /// AP seed runs only the ApContract subclasses defined in this
    /// assembly. Validated by AP_Career_Probe (see notes/career_spike.md
    /// §Q3.H3.1): ContractSystem.ContractTypes and MandatoryTypes are
    /// runtime-mutable lists; Withdraw() on live instances fires
    /// GameEvents.Contract.onFinished naturally. No Harmony required.
    ///
    /// Whitelist approach (safer than blacklist): any Type that is NOT
    /// a subclass of ApContract gets removed. New stock or third-party
    /// contract types added in the future are automatically excluded
    /// without code changes.
    ///
    /// Idempotent. Re-suppressing after a no-op pass is cheap.
    /// Caveat from the probe: Mission Control UI doesn't auto-refresh
    /// after Withdraw() — visually-stale contracts in MC clear only when
    /// the player switches tabs or reopens the building.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class StockContractSuppressor : MonoBehaviour
    {
        // Once per session — ContractSystem.ContractTypes is rebuilt by
        // KSP from assembly reflection at startup, so we re-suppress on
        // each session, but only ONCE per session per save.
        private string _suppressedSaveFolder = null;

        void Awake()
        {
            DontDestroyOnLoad(this);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnSceneReady);
        }

        void OnDestroy()
        {
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnSceneReady);
        }

        // The player connects to AP from inside SpaceCenter, AFTER
        // onLevelWasLoadedGUIReady has already fired — so the scene-event path
        // misses the connect. Re-check every frame in SpaceCenter; the
        // per-save guard in OnSceneReady makes this a no-op once suppression
        // has run, so the cost is one FindObjectOfType until connected.
        void Update()
        {
            if (HighLogic.LoadedScene == GameScenes.SPACECENTER)
                OnSceneReady(GameScenes.SPACECENTER);
        }

        private void OnSceneReady(GameScenes scene)
        {
            // ContractSystem.Instance is non-null only in Career and only
            // after a save is loaded. SpaceCenter is the earliest scene
            // where Instance is reliably alive. Tracking Station and
            // MissionControl also work; pinning on SC keeps the trigger
            // point unambiguous.
            if (scene != GameScenes.SPACECENTER) return;
            if (HighLogic.CurrentGame == null) return;
            if (HighLogic.CurrentGame.Mode != Game.Modes.CAREER) return;

            // Don't re-suppress for the same save. If the player switches
            // saves we want to re-evaluate (a different save may not be
            // connected to AP and shouldn't have stock contracts gone).
            string save = HighLogic.SaveFolder;
            if (_suppressedSaveFolder == save) return;

            // Pre-connect guard. The full suppression below is gated on
            // IsConnected, but KSP registers every Contract subclass — including
            // our abstract ApContract base — in ContractSystem.ContractTypes via
            // startup reflection, and the stock generation daemon runs
            // independently of AP. If it picks the abstract base it calls
            // Activator.CreateInstance on it and throws MissingMethodException
            // ("Default constructor not found") — long before we ever connect.
            // Strip the abstract AP types every frame until connected so the
            // daemon can never hit one. Concrete AP contracts are safe: they
            // self-skip in GeneratePopulate/MeetRequirements when unbound.
            int strippedAbstract = StripAbstractApContractTypes();
            if (strippedAbstract > 0)
                Debug.Log($"[KSP-AP] StockContractSuppressor: stripped "
                        + $"{strippedAbstract} abstract AP contract type(s) from "
                        + "the stock generation pool (pre-connect guard).");

            // Only suppress when AP is actually driving this session.
            // Without AP, stock contracts are still the player's only
            // gameplay loop — removing them would soft-lock vanilla Career.
            var mod = UnityEngine.Object.FindObjectOfType<KSPArchipelagoMod>();
            if (mod == null || !mod.IsConnected) return;

            try
            {
                int removed = SuppressNonApContractTypes();
                int withdrawn = WithdrawNonApLiveContracts();
                _suppressedSaveFolder = save;
                Debug.Log($"[KSP-AP] StockContractSuppressor: save='{save}' "
                        + $"removed={removed} types, withdrew={withdrawn} live contracts. "
                        + "Mission Control may show stale entries until reopened.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KSP-AP] StockContractSuppressor failed: {ex}");
            }
        }

        /// <summary>
        /// Remove every Type from ContractSystem.ContractTypes and
        /// MandatoryTypes that is NOT a subclass of ApContract. Returns
        /// the count removed.
        /// </summary>
        private static int SuppressNonApContractTypes()
        {
            if (ContractSystem.ContractTypes == null) return 0;

            // Snapshot first — modifying while iterating is unsafe.
            // Remove everything that isn't a CONCRETE ApContract subclass. The
            // abstract base is normally already gone (StripAbstractApContractTypes
            // runs pre-connect), but the IsAbstract clause keeps this pass
            // correct on its own.
            List<Type> targets = ContractSystem.ContractTypes
                .Where(t => t != null
                            && (!typeof(ApContract).IsAssignableFrom(t) || t.IsAbstract))
                .ToList();

            RemoveContractTypes(targets);
            return targets.Count;
        }

        /// <summary>
        /// Remove abstract ApContract-derived types (e.g. the ApContract base)
        /// from ContractSystem.ContractTypes. KSP registers every Contract
        /// subclass via startup reflection, and its stock generation daemon —
        /// which runs independently of the AP connection — throws
        /// MissingMethodException if it tries to Activator.CreateInstance an
        /// abstract type. Always safe to run: concrete AP contracts self-skip
        /// generation when unbound, so only the abstract types need removing.
        /// Scoped to ApContract-derived types so vanilla / third-party contract
        /// types are never touched when AP isn't driving. Returns count removed.
        /// </summary>
        private static int StripAbstractApContractTypes()
        {
            List<Type> types = ContractSystem.ContractTypes;
            if (types == null) return 0;

            // Manual scan (no LINQ allocation) so the steady-state per-frame
            // call in the never-connected case stays allocation-free.
            List<Type> targets = null;
            foreach (Type t in types)
            {
                if (t != null && t.IsAbstract && typeof(ApContract).IsAssignableFrom(t))
                {
                    if (targets == null) targets = new List<Type>();
                    targets.Add(t);
                }
            }
            if (targets == null) return 0;

            RemoveContractTypes(targets);
            return targets.Count;
        }

        /// <summary>
        /// Remove each target Type from ContractSystem.ContractTypes and
        /// MandatoryTypes. While-loops guard against duplicate registrations
        /// (the career probe observed the same Type listed more than once).
        /// </summary>
        private static void RemoveContractTypes(List<Type> targets)
        {
            foreach (Type t in targets)
            {
                while (ContractSystem.ContractTypes.Contains(t))
                {
                    ContractSystem.ContractTypes.Remove(t);
                }
                if (ContractSystem.MandatoryTypes != null)
                {
                    while (ContractSystem.MandatoryTypes.Contains(t))
                    {
                        ContractSystem.MandatoryTypes.Remove(t);
                    }
                }
            }
        }

        /// <summary>
        /// Withdraw any currently-offered or active contracts whose Type
        /// is no longer in ContractTypes. Returns count withdrawn.
        /// </summary>
        private static int WithdrawNonApLiveContracts()
        {
            int withdrawn = 0;
            if (ContractSystem.Instance == null
                || ContractSystem.Instance.Contracts == null) return 0;

            HashSet<Type> allowed = new HashSet<Type>(
                ContractSystem.ContractTypes ?? Enumerable.Empty<Type>());

            // Snapshot — Withdraw mutates the live list.
            List<Contract> targets = ContractSystem.Instance.Contracts
                .Where(c => c != null
                            && !allowed.Contains(c.GetType())
                            && (c.ContractState == Contract.State.Offered
                                || c.ContractState == Contract.State.Active))
                .ToList();

            foreach (Contract c in targets)
            {
                try
                {
                    c.Withdraw();
                    withdrawn++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[KSP-AP] Withdraw failed for '{c.GetType().FullName}': {ex.Message}");
                }
            }
            return withdrawn;
        }
    }
}
