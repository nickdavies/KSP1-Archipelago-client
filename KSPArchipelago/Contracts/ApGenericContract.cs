using System;
using Contracts;
using KSPArchipelago.Contracts.Primitives;
using UnityEngine;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// The single data-driven AP contract. One instance is bound to one
    /// server-emitted <see cref="ContractSlotSpec"/>; in <see cref="GeneratePopulate"/>
    /// it builds a KSP parameter tree from that spec's <c>parameters</c> via
    /// the <see cref="ContractPrimitiveRegistry"/>, and on completion reports
    /// the spec's AP <c>location</c>.
    ///
    /// A single generic subclass hosting a declarative param tree is
    /// intentional: new contract types that recombine existing primitives need
    /// no client release. StockContractSuppressor whitelists ApContract
    /// subclasses, so this is offered/loaded automatically.
    ///
    /// Binding is push-only: <see cref="ApContractManager.TryForceOffer"/> sets
    /// <see cref="PendingSpec"/> immediately before Contract.Generate, and the
    /// contract is force-accepted to ACTIVE so its parameters track at once.
    /// A null PendingSpec makes GeneratePopulate return false, so KSP's stock
    /// generation poll can never spawn a stray, un-bound contract.
    /// </summary>
    public class ApGenericContract : ApContract
    {
        /// <summary>
        /// Main-thread handoff from <see cref="ApContractManager.TryForceOffer"/>.
        /// KSP's Contract.Generate calls our Generate() synchronously, so this
        /// is read and cleared within a single call — never serialised.
        /// </summary>
        internal static ContractSlotSpec PendingSpec;

        /// <summary>AP location this instance reports on completion. Persisted.</summary>
        public string BoundLocation { get; private set; }

        private ContractSlotSpec Spec =>
            string.IsNullOrEmpty(BoundLocation) ? null
                : ApContractManager.GetByLocation(BoundLocation);

        protected override bool GeneratePopulate()
        {
            // Two creation paths, both fine:
            //   - KSP's stock generation poll (runs inside ContractSystem.Update,
            //     always post-load → no scene-load race): PendingSpec is null,
            //     so we bind the next offerable spec.
            // Force-offer is the SOLE creation path: ApContractManager sets
            // PendingSpec immediately before Contract.Generate (and adds the
            // result to ContractSystem one-at-a-time, so dedup is exact). KSP's
            // stock poll must NOT create contracts — it batch-generates several
            // per cycle before adding any, so every one binds the same offerable
            // spec (dedup can't see the in-flight siblings) → 20-30 duplicates.
            ContractSlotSpec spec = PendingSpec;
            if (spec == null) return false;

            // Schema/kind were validated at connect (HandleConnect → registry
            // ValidateSpec); registry.Build still throws on anything unexpected,
            // which ApContract.Generate catches and turns into "don't offer".
            BoundLocation = spec.Location;

            // Implicit AND: every parameter must complete. KSP gives us the
            // boolean combinators for free; the server orders the children.
            foreach (var paramSpec in spec.Parameters)
            {
                ContractParameter param = ContractPrimitiveRegistry.Build(paramSpec);
                if (param != null) AddParameter(param);
            }

            Debug.Log($"[KSP-AP] ApGenericContract bound to '{BoundLocation}' "
                    + $"with {spec.Parameters.Count} parameter(s)");
            return true;
        }

        // True during a force-offer (PendingSpec set) so Contract.Generate
        // proceeds, and forever after for a bound contract so KSP never
        // auto-cancels an active one. Returns false for an unbound fresh
        // instance from KSP's stock poll → the poll can't spawn duplicates.
        public override bool MeetRequirements()
            => PendingSpec != null || !string.IsNullOrEmpty(BoundLocation);

        protected override void OnCompleted()
        {
            base.OnCompleted();
            ReportAndLog(BoundLocation);
        }

        protected override string GetTitle()
            => Spec?.Title ?? BoundLocation ?? "AP Contract";

        protected override string GetSynopsys()
            => Spec?.Synopsis ?? GetTitle();

        protected override string GetDescription()
            => Spec?.Synopsis ?? GetTitle();

        protected override string GetHashString()
            => "ApGenericContract|" + (BoundLocation ?? "<unbound>");

        // Child parameters are serialised/restored by KSP itself; we only need
        // to persist which AP location this contract reports on completion.
        protected override void OnSave(ConfigNode node)
        {
            if (!string.IsNullOrEmpty(BoundLocation))
                node.AddValue("ap_location_name", BoundLocation);
        }

        protected override void OnLoad(ConfigNode node)
        {
            BoundLocation = node.GetValue("ap_location_name");
            if (string.IsNullOrEmpty(BoundLocation))
                Debug.LogWarning("[KSP-AP] ApGenericContract loaded with no bound "
                               + "location — it will not report on completion.");
        }
    }
}
