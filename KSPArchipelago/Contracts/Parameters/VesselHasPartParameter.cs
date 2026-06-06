using Contracts;
using UnityEngine;

namespace KSPArchipelago.Contracts.Parameters
{
    /// <summary>
    /// Polls FlightGlobals.ActiveVessel.parts each frame and SetComplete
    /// once a part whose name matches RequiredPartName is found on board.
    /// Used by anchored contracts that require a specific part (Science
    /// Jr satellite, etc.) and procedural contracts that may impose a
    /// part presence.
    ///
    /// PartName matching is exact against Part.partInfo.name (the
    /// AvailablePart's internal name, NOT the title). Generation side
    /// emits the internal name in slot_data so we don't have to deal
    /// with display-name localization differences.
    ///
    /// Stock KSP1 has no built-in equivalent — PartTest is for the
    /// "test this part" contract type which has additional repeat/situation
    /// machinery we don't want here.
    /// </summary>
    public class VesselHasPartParameter : ContractParameter
    {
        public string RequiredPartName;

        public VesselHasPartParameter() { }
        public VesselHasPartParameter(string requiredPartName)
        {
            RequiredPartName = requiredPartName ?? "";
        }

        protected override string GetTitle()
            => $"Vessel must carry: {RequiredPartName}";

        protected override string GetHashString()
            => $"VesselHasPart|{RequiredPartName}";

        protected override void OnUpdate()
        {
            if (Root == null || Root.ContractState != Contract.State.Active) return;
            if (state == ParameterState.Complete) return;
            if (string.IsNullOrEmpty(RequiredPartName)) return;
            var v = FlightGlobals.ActiveVessel;
            if (v?.parts == null) return;
            for (int i = 0; i < v.parts.Count; i++)
            {
                var p = v.parts[i];
                if (p == null || p.partInfo == null) continue;
                if (p.partInfo.name == RequiredPartName)
                {
                    SetComplete();
                    return;
                }
            }
        }

        protected override void OnLoad(ConfigNode node)
        {
            RequiredPartName = node.GetValue("required_part") ?? "";
        }

        protected override void OnSave(ConfigNode node)
        {
            node.AddValue("required_part", RequiredPartName ?? "");
        }
    }
}
