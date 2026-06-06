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
            int removed = 0;
            if (ContractSystem.ContractTypes == null) return 0;

            // Snapshot first — modifying while iterating is unsafe.
            List<Type> targets = ContractSystem.ContractTypes
                .Where(t => t != null && !typeof(ApContract).IsAssignableFrom(t))
                .ToList();

            foreach (Type t in targets)
            {
                // While-loop in case the same Type is duplicated (probe
                // saw this pattern; defensive).
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
                removed++;
            }
            return removed;
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
