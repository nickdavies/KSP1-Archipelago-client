using System.Collections.Generic;
using Contracts;
using UnityEngine;

namespace KSPArchipelago.Contracts.Parameters
{
    /// <summary>
    /// Completes once the active vessel carries AT LEAST ONE of
    /// <see cref="AcceptedPartNames"/> (has-any / OR semantics). The generator
    /// resolves a part CATEGORY (e.g. "drill") to an explicit cfg-name list so
    /// the client never needs part knowledge — this is the binding for the
    /// `has_any_part` primitive.
    ///
    /// Matching is exact against Part.partInfo.name (AvailablePart internal
    /// cfg-name, NOT the display title); the generator emits cfg-names so we
    /// avoid localisation differences. Stock KSP1 has no equivalent — PartTest
    /// carries test/repeat/situation machinery we don't want here.
    /// </summary>
    public class VesselHasPartParameter : ContractParameter
    {
        /// <summary>Any one of these cfg-names on the active vessel completes the parameter.</summary>
        public List<string> AcceptedPartNames = new List<string>();
        /// <summary>Category label for display only (e.g. "drill"). Optional.</summary>
        public string Label = "";

        public VesselHasPartParameter() { }

        public VesselHasPartParameter(IEnumerable<string> partNames, string label = "")
        {
            if (partNames != null) AcceptedPartNames = new List<string>(partNames);
            Label = label ?? "";
        }

        protected override string GetTitle()
        {
            if (!string.IsNullOrEmpty(Label))
                return $"Vessel must carry a {Label.Replace('_', ' ')}";
            return "Vessel must carry: " + string.Join(" or ", AcceptedPartNames.ToArray());
        }

        protected override string GetHashString()
            => "VesselHasPart|" + string.Join("|", AcceptedPartNames.ToArray());

        protected override void OnUpdate()
        {
            if (Root == null || Root.ContractState != Contract.State.Active) return;
            if (state == ParameterState.Complete) return;
            if (AcceptedPartNames.Count == 0) return;
            var v = FlightGlobals.ActiveVessel;
            if (v?.parts == null) return;
            for (int i = 0; i < v.parts.Count; i++)
            {
                var p = v.parts[i];
                if (p == null || p.partInfo == null) continue;
                if (AcceptedPartNames.Contains(p.partInfo.name))
                {
                    SetComplete();
                    return;
                }
            }
        }

        protected override void OnLoad(ConfigNode node)
        {
            AcceptedPartNames = new List<string>(node.GetValues("required_part"));
            Label = node.GetValue("label") ?? "";
        }

        protected override void OnSave(ConfigNode node)
        {
            foreach (string n in AcceptedPartNames)
                node.AddValue("required_part", n);
            if (!string.IsNullOrEmpty(Label))
                node.AddValue("label", Label);
        }
    }
}
