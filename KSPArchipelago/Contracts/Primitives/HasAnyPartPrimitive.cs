using System;
using System.Collections.Generic;
using Contracts;
using KSPArchipelago.Contracts.Parameters;
using Newtonsoft.Json.Linq;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind": "has_any_part", "parts": ["MiniDrill","RadialDrill"], "label": "drill" }</c>
    ///
    /// Maps to the mod's <see cref="VesselHasPartParameter"/>: completes when
    /// the active vessel carries at least one of the listed parts. The server
    /// has already resolved a part category to this explicit cfg-name list, so
    /// the client never needs part/DLC knowledge. <c>label</c> is the category
    /// name, used only for the parameter title.
    /// </summary>
    public sealed class HasAnyPartPrimitive : IContractPrimitive
    {
        public string Kind => "has_any_part";

        public ContractParameter Build(JObject spec)
        {
            var parts = new List<string>();
            if (spec["parts"] is JArray arr)
            {
                foreach (JToken t in arr)
                {
                    string name = (string)t;
                    if (!string.IsNullOrEmpty(name)) parts.Add(name);
                }
            }
            if (parts.Count == 0)
                throw new FormatException("has_any_part primitive has no 'parts'");

            string label = (string)spec["label"] ?? "";
            return new VesselHasPartParameter(parts, label);
        }
    }
}
