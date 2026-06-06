using System;
using Contracts;
using UnityEngine;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// Common abstract base for both anchored and procedural AP contracts.
    /// Holds shared lifecycle plumbing — stock OFFERED flow, declinable,
    /// cancellable, no expiry / no deadline — and the display-string
    /// boilerplate KSP requires every Contract subclass to implement.
    ///
    /// Concrete subclasses choose one of the two roles:
    ///   - ApAnchoredContract — bound to a server-emitted slot spec
    ///     (test_part, satellite_with_part)
    ///   - ApProceduralContract — generated randomly within the envelope
    ///     (tourist, satellite, mass_delivery, altitude_or_orbit), and
    ///     consumes from per-type AP location pools on completion via
    ///     ApProceduralCheckManager.
    ///
    /// Both feed into StockContractSuppressor's whitelist:
    /// `!typeof(ApContract).IsAssignableFrom(t)` removes everything that
    /// isn't one of ours.
    /// </summary>
    public abstract class ApContract : Contract
    {
        protected override bool Generate()
        {
            expiryType = DeadlineType.None;
            deadlineType = DeadlineType.None;
            // Default prestige. Subclasses can override before AddParameter
            // calls if they want different reward scaling.
            prestige = ContractPrestige.Trivial;
            SetReputation(completion: 5f, failure: -2f);
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
