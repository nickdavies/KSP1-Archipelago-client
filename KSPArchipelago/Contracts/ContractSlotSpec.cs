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
    ///   "location":  "Contract: Mine Ore on Mun",   // reported on completion
    ///   "title":     "Mine 100 ore on Mun",
    ///   "synopsis":  "Extract 100 units of ore from the surface of Mun.",
    ///   "schema":    1,                              // primitive-registry version
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
        /// <summary>AP location reported when the contract completes.</summary>
        public string Location { get; private set; }
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
            string location = (string)obj["location"];
            if (string.IsNullOrEmpty(location))
                throw new FormatException($"contract '{item}' missing 'location'");

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
                Location = location,
                Title = (string)obj["title"] ?? location,
                Synopsis = (string)obj["synopsis"] ?? (string)obj["title"] ?? location,
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
