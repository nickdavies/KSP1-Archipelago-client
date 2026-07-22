using Contracts;
using UnityEngine;

namespace KSPArchipelago.Contracts.Parameters
{
    /// <summary>
    /// Completes when the player docks two SEPARATELY-LAUNCHED craft in the
    /// sphere of influence of a target body. Replicates stock
    /// KSPAchievements.Docking's onPartCouple filter (Assembly-CSharp
    /// decompiled :846923): reject a couple where either half is EVA, reject a
    /// couple where both halves share a <c>missionID</c> (both parts from one
    /// launch don't count — the objective is joining two independently-launched
    /// craft), and require the dock happens at the target body.
    ///
    /// Unlike a stock milestone/achievement, this is a <see cref="ContractParameter"/>
    /// that only subscribes to <c>onPartCouple</c> while its contract is Active.
    /// That makes the objective POST-ACTIVATION by construction (a dock performed
    /// before the contract was offered can never satisfy it) and repeatable
    /// across contracts (every future docking fires the event afresh) — the two
    /// requirements B1 was designed around.
    /// </summary>
    public class ApDockingParameter : ContractParameter
    {
        /// <summary>Body whose SOI the dock must occur in. Persisted.</summary>
        public string BodyName = "";
        private bool _eventHooked;

        public ApDockingParameter() { }

        public ApDockingParameter(string bodyName)
        {
            BodyName = bodyName ?? "";
        }

        protected override string GetTitle()
            => $"Dock two vessels in the sphere of influence of {BodyName}";

        protected override string GetHashString()
            => "ApDocking|" + BodyName;

        protected override void OnRegister()
        {
            // Only listen while the contract is Active (Register is what fires
            // this) — that is precisely what makes the dock post-activation and
            // repeatable. OnUnregister on completion/withdrawal detaches us.
            GameEvents.onPartCouple.Add(OnPartCouple);
            _eventHooked = true;
        }

        protected override void OnUnregister()
        {
            if (!_eventHooked) return;
            GameEvents.onPartCouple.Remove(OnPartCouple);
            _eventHooked = false;
        }

        // Stock Docking.OnPartsCoupled filter, replicated exactly.
        private void OnPartCouple(GameEvents.FromToAction<Part, Part> action)
        {
            if (state == ParameterState.Complete) return;
            if (Root == null || Root.ContractState != Contract.State.Active) return;
            Part from = action.from, to = action.to;
            if (from == null || to == null) return;
            Vessel fromVessel = from.vessel, toVessel = to.vessel;
            if (fromVessel == null || toVessel == null) return;
            // Reject EVA couples (a Kerbal grabbing a ladder/part is not a dock).
            if (fromVessel.isEVA || toVessel.isEVA) return;
            // Reject a single launch docking to itself — both halves must come
            // from two separately-launched craft (different missionIDs).
            if (from.missionID == to.missionID) return;
            CelestialBody body = FlightGlobals.GetBodyByName(BodyName);
            if (body == null) return;
            if (fromVessel.mainBody != body) return;
            SetComplete();
        }

        protected override void OnSave(ConfigNode node)
        {
            node.AddValue("body", BodyName);
        }

        protected override void OnLoad(ConfigNode node)
        {
            BodyName = node.GetValue("body") ?? "";
        }
    }
}
