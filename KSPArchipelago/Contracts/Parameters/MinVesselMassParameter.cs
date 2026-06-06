using Contracts;
using UnityEngine;

namespace KSPArchipelago.Contracts.Parameters
{
    /// <summary>
    /// Polls FlightGlobals.ActiveVessel.GetTotalMass() each frame while
    /// the parent contract is active. Sets Complete when the vessel's
    /// total mass meets or exceeds MinMassTonnes. Used by procedural
    /// mass_delivery contracts and any anchored contract that wants to
    /// gate on payload mass.
    ///
    /// Stock KSP1 does NOT ship a built-in "vessel has min mass"
    /// ContractParameter — the closest is the IPreFlightTest
    /// CraftWithinMassLimits, which is editor-side only. Hence this
    /// poll-based custom param.
    /// </summary>
    public class MinVesselMassParameter : ContractParameter
    {
        public float MinMassTonnes;

        public MinVesselMassParameter() { }
        public MinVesselMassParameter(float minMassTonnes)
        {
            MinMassTonnes = minMassTonnes;
        }

        protected override string GetTitle()
            => $"Vessel mass at least {MinMassTonnes:F1} t";

        protected override string GetHashString()
            => $"MinVesselMass|{MinMassTonnes:F2}";

        protected override void OnUpdate()
        {
            if (Root == null || Root.ContractState != Contract.State.Active) return;
            if (state == ParameterState.Complete) return;
            var v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            if (v.GetTotalMass() + 0.001f >= MinMassTonnes)
            {
                SetComplete();
            }
        }

        protected override void OnLoad(ConfigNode node)
        {
            float.TryParse(node.GetValue("min_mass_t"), out MinMassTonnes);
        }

        protected override void OnSave(ConfigNode node)
        {
            node.AddValue("min_mass_t", MinMassTonnes);
        }
    }
}
