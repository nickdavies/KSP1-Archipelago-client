using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// One AP-driven contract slot, materialised from slot_data. The
    /// generation side emits one of these per contract location it wants
    /// the player to be able to fulfil. The client picks slots out of
    /// the pool and exposes them as KSP contracts when MeetRequirements
    /// is satisfied.
    ///
    /// Wire format (one entry in slot_data["contract_slots"]):
    /// <code>
    /// {
    ///   "ap_location_name": "Contract: Orbit Mun w/ Payload",
    ///   "contract_type":    "mass_delivery",
    ///   "params": {
    ///     "destination_body": "Mun",
    ///     "destination_kind": "orbit",
    ///     "payload_kg":       500
    ///   },
    ///   "requires": {
    ///     "all": ["Orbit Kerbin", { "any": ["Flyby Mun", "Flyby Minmus"] }]
    ///   },
    ///   "pays_funds":   false,
    ///   "funds_reward": 0,
    ///   "title":        "Deliver 500 kg payload to Mun orbit",
    ///   "synopsis":     "Drop a dummy mass package in Mun orbit."
    /// }
    /// </code>
    /// `requires` is parsed via ContractRequirement; missing/null means
    /// always available. `params` is opaque here — each contract type
    /// (ApAltitudeOrOrbitContract, ApMassDeliveryContract, ...) reads
    /// what it needs.
    /// </summary>
    public sealed class ContractSlotSpec
    {
        public string ApLocationName { get; private set; }
        public string ContractType   { get; private set; }
        public JObject Params        { get; private set; }
        public ContractRequirement Requires { get; private set; }
        public bool PaysFunds        { get; private set; }
        public float FundsReward     { get; private set; }
        public string Title          { get; private set; }
        public string Synopsis       { get; private set; }

        public static ContractSlotSpec Parse(JObject obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            string ap = (string)obj["ap_location_name"];
            if (string.IsNullOrEmpty(ap))
                throw new FormatException("ContractSlotSpec missing ap_location_name");
            string type = (string)obj["contract_type"];
            if (string.IsNullOrEmpty(type))
                throw new FormatException($"ContractSlotSpec '{ap}' missing contract_type");

            JObject parameters = obj["params"] as JObject ?? new JObject();
            ContractRequirement req = ContractRequirement.Parse(obj["requires"]);

            bool paysFunds = (bool?)obj["pays_funds"] ?? false;
            float fundsReward = (float?)obj["funds_reward"] ?? 0f;
            string title = (string)obj["title"] ?? ap;
            string syn   = (string)obj["synopsis"] ?? title;

            return new ContractSlotSpec
            {
                ApLocationName = ap,
                ContractType   = type,
                Params         = parameters,
                Requires       = req,
                PaysFunds      = paysFunds,
                FundsReward    = fundsReward,
                Title          = title,
                Synopsis       = syn,
            };
        }

        /// <summary>
        /// Parse the top-level "contract_slots" array from slot_data.
        /// Returns an empty list on null/missing — slot_data without
        /// contracts is a valid configuration (e.g. early-development
        /// seeds).
        /// </summary>
        public static List<ContractSlotSpec> ParseAll(JToken token)
        {
            var result = new List<ContractSlotSpec>();
            if (token == null || token.Type == JTokenType.Null) return result;
            if (token.Type != JTokenType.Array)
                throw new FormatException(
                    $"slot_data.contract_slots must be array, got {token.Type}");
            int idx = 0;
            foreach (JToken entry in (JArray)token)
            {
                if (!(entry is JObject jo))
                    throw new FormatException(
                        $"contract_slots[{idx}] must be object, got {entry.Type}");
                result.Add(Parse(jo));
                idx++;
            }
            return result;
        }
    }
}
