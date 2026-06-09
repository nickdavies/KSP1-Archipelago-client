using System;
using Contracts;
using UnityEngine;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// Common base for AP contracts. Holds shared lifecycle plumbing — stock
    /// OFFERED flow, declinable, cancellable, no expiry / no deadline — and the
    /// display-string boilerplate KSP requires every Contract subclass to
    /// implement.
    ///
    /// The only concrete subclass is <see cref="ApGenericContract"/>, the
    /// data-driven contract bound to a server-emitted ContractSlotSpec. This
    /// base stays separate so StockContractSuppressor's whitelist
    /// (`!typeof(ApContract).IsAssignableFrom(t)`) keeps a stable anchor type
    /// even if more contract subclasses are added later.
    /// </summary>
    public abstract class ApContract : Contract
    {
        protected override bool Generate()
        {
            expiryType = DeadlineType.None;
            deadlineType = DeadlineType.None;
            prestige = ContractPrestige.Trivial;
            // No in-game rewards: the career economy is AP-hacked, so a
            // reputation reward is pointless — and awarding it would route
            // through KSP's ModifyReputationDelta / CurrencyModifierQuery
            // pipeline (the same one that NREs on direct manipulation).
            // AP grants the real reward when the location check fires.
            SetReputation(completion: 0f, failure: 0f);
            SetFunds(advance: 0f, completion: 0f, failure: 0f);
            SetScience(completion: 0f);
            try
            {
                return GeneratePopulate();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KSP-AP] {GetType().Name}.GeneratePopulate failed: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Subclass hook: pick params, AddParameter() the success-condition
        /// parameter tree, set funds if applicable. Return true to spawn
        /// the contract, false to skip this Generate cycle (KSP retries).
        /// </summary>
        protected abstract bool GeneratePopulate();

        public override bool CanBeCancelled() => true;
        public override bool CanBeDeclined() => true;

        protected override string GetTitle() => GetType().Name;
        protected override string GetSynopsys() => GetTitle();
        protected override string GetDescription() => GetTitle();
        protected override string MessageCompleted() => $"AP contract complete: {GetTitle()}";

        // GetHashString must be unique enough to keep KSP from deduplicating
        // distinct instances. Subclasses override to mix in their slot or
        // generated-params hash.
        protected override string GetHashString() => GetType().Name + "|" + GetHashCode();

        protected override void OnLoad(ConfigNode node)   { }
        protected override void OnSave(ConfigNode node)   { }

        protected void ReportAndLog(string apLocationName)
        {
            if (string.IsNullOrEmpty(apLocationName)) return;
            try
            {
                var mod = UnityEngine.Object.FindObjectOfType<KSPArchipelagoMod>();
                mod?.Tracker?.ReportLocation(apLocationName);
                Debug.Log($"[KSP-AP] {GetType().Name} reported location '{apLocationName}'");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KSP-AP] {GetType().Name} ReportLocation failed: {ex}");
            }
        }
    }
}
