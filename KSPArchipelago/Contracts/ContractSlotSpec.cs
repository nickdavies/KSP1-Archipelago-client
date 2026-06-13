using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// One server-emitted contract, materialised from slot_data["contracts"].
    /// The generator owns all contract definitions; the client is a pure
    /// actuator that builds a KSP ContractParameter tree from <see cref="Parameters"/>
    /// using the primitive registry (see Contracts/Primitives).
    ///
    /// Wire format (one entry in slot_data["contracts"]):
    /// <code>
    /// {
    ///   "item":      "Contract: Mine Ore on Mun",   // AP item that unlocks it
    ///   "locations": ["Contract: Mine Ore on Mun 1",// reported on completion;
    ///                 "Contract: Mine Ore on Mun 2"],//  non-goal: 2, goal: 1
    ///   "title":     "Mine 100 ore on Mun",
    ///   "synopsis":  "Extract 100 units of ore from the surface of Mun.",
    ///   "schema":    3,                              // primitive-registry version
    ///   "is_goal":   false,                          // goal contracts are 1:1
    ///   "parameters": [                             // implicit-AND success conditions
    ///     { "kind": "situation", "situation": "landed", "body": "Mun" },
    ///     { "kind": "resource",  "resource": "Ore", "min": 100 }
    ///   ]
    /// }
    /// </code>
    ///
    /// `parameters` is left as raw JObjects here — the primitive registry is
    /// the single compile-checked surface that interprets each `kind`. Keeping
    /// the spec dumb means new contract types that recombine existing
    /// primitives need no change to this class.
    /// </summary>
    public sealed class ContractSlotSpec
    {
        /// <summary>AP item whose receipt offers this contract.</summary>
        public string Item { get; private set; }
        /// <summary>
        /// AP reward location(s) reported when the contract completes. Non-goal
        /// contracts carry two slots; goal contracts one. Always non-empty.
        /// </summary>
        public IList<string> Locations { get; private set; }
        /// <summary>
        /// Primary / binding location (slot 1). Used as the contract's stable
        /// identity for offering, dedup, save/restore, and lookup; the sibling
        /// slot (if any) is reported alongside it but never bound to.
        /// </summary>
        public string Location => Locations[0];
        /// <summary>True for a goal contract (1:1, no slot suffix).</summary>
        public bool IsGoal { get; private set; }
        public string Title { get; private set; }
        public string Synopsis { get; private set; }
        /// <summary>Primitive-registry version. The client rejects unknown values.</summary>
        public int Schema { get; private set; }
        /// <summary>Raw parameter specs, in implicit-AND order. Interpreted by the registry.</summary>
        public IList<JObject> Parameters { get; private set; }

        public static ContractSlotSpec Parse(JObject obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            string item = (string)obj["item"];
            if (string.IsNullOrEmpty(item))
                throw new FormatException("contract entry missing 'item'");

            var locations = new List<string>();
            if (obj["locations"] is JArray locArr)
            {
                foreach (JToken lt in locArr)
                {
                    string ln = (string)lt;
                    if (!string.IsNullOrEmpty(ln)) locations.Add(ln);
                }
            }
            if (locations.Count == 0)
                throw new FormatException(
                    $"contract '{item}' missing non-empty 'locations'");

            int schema = (int?)obj["schema"]
                ?? throw new FormatException($"contract '{item}' missing 'schema'");

            var parameters = new List<JObject>();
            if (obj["parameters"] is JArray paramArr)
            {
                int i = 0;
                foreach (JToken p in paramArr)
                {
                    if (!(p is JObject po))
                        throw new FormatException(
                            $"contract '{item}' parameters[{i}] must be an object");
                    parameters.Add(po);
                    i++;
                }
            }

            return new ContractSlotSpec
            {
                Item = item,
                Locations = locations,
                IsGoal = (bool?)obj["is_goal"] ?? false,
                Title = (string)obj["title"] ?? locations[0],
                Synopsis = (string)obj["synopsis"] ?? (string)obj["title"] ?? locations[0],
                Schema = schema,
                Parameters = parameters,
            };
        }

        /// <summary>
        /// Parse the top-level "contracts" array. An empty/absent array is a
        /// valid configuration (a seed with no contract items).
        /// </summary>
        public static List<ContractSlotSpec> ParseAll(JToken token)
        {
            var result = new List<ContractSlotSpec>();
            if (token == null || token.Type == JTokenType.Null) return result;
            if (token.Type != JTokenType.Array)
                throw new FormatException(
                    $"slot_data.contracts must be an array, got {token.Type}");
            int idx = 0;
            foreach (JToken entry in (JArray)token)
            {
                if (!(entry is JObject jo))
                    throw new FormatException(
                        $"contracts[{idx}] must be an object, got {entry.Type}");
                result.Add(Parse(jo));
                idx++;
            }
            return result;
        }
    }
}
