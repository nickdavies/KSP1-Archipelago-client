using Contracts;
using UnityEngine;

namespace KSPArchipelago.Contracts.Parameters
{
    /// <summary>
    /// Polls FlightGlobals.ActiveVessel's crew count and SetComplete when
    /// it reaches MinCrew. Used by procedural tourist contracts (the
    /// crew are tourists; we don't gate on the kerbal type, just the
    /// headcount, to keep tourist spawning out of v1 scope).
    ///
    /// AcquireCrew is the stock equivalent but is tied to the Kerbal
    /// Hire / Astronaut Complex mechanic for crew acquisition specifically,
    /// not "vessel currently has N crew members aboard."
    /// </summary>
    public class MinCrewCountParameter : ContractParameter
    {
        public int MinCrew;

        public MinCrewCountParameter() { }
        public MinCrewCountParameter(int minCrew)
        {
            MinCrew = minCrew;
        }

        protected override string GetTitle()
            => $"Vessel must carry at least {MinCrew} crew";

        protected override string GetHashString() => $"MinCrew|{MinCrew}";

        protected override void OnUpdate()
        {
            if (Root == null || Root.ContractState != Contract.State.Active) return;
            if (state == ParameterState.Complete) return;
            var v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            if (v.GetCrewCount() >= MinCrew)
            {
                SetComplete();
            }
        }

        protected override void OnLoad(ConfigNode node)
        {
            int.TryParse(node.GetValue("min_crew"), out MinCrew);
        }

        protected override void OnSave(ConfigNode node)
        {
            node.AddValue("min_crew", MinCrew);
        }
    }
}
