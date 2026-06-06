using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// Per-body, per-destination-kind capability envelope. Drives BOTH:
    ///   - Procedural contract generation (what (body, kind, payload_kg)
    ///     tuples a procedural subclass may pick when KSP calls Generate)
    ///   - Anchored contract payload sizing (anchored slot specs name the
    ///     body+kind+part; the envelope provides the max_payload_kg the
    ///     contract is sized against)
    ///
    /// Wire format (slot_data["contract_envelope"]):
    /// <code>
    /// {
    ///   "Kerbin": {
    ///     "altitude":   { "max_altitude_m": 70000, "max_payload_kg": 50 },
    ///     "suborbital": { "max_payload_kg": 100 },
    ///     "orbit":      { "max_payload_kg": 500 },
    ///     "polar":      null
    ///   },
    ///   "Mun": {
    ///     "flyby":      { "max_payload_kg": 0 }
    ///   }
    /// }
    /// </code>
    /// A null entry under a body means "not unlocked" for that kind.
    /// max_payload_kg of 0 is valid and means "no extra dummy mass beyond
    /// the required parts" (an empty satellite or tourist still qualifies).
    /// </summary>
    public sealed class ContractEnvelope
    {
        public sealed class KindEntry
        {
            public float MaxPayloadKg { get; private set; }
            public float MaxAltitudeM { get; private set; }   // only meaningful for "altitude" kind
            public float InclinationDegMin { get; private set; }
            public float InclinationDegMax { get; private set; }

            public static KindEntry Parse(JObject obj)
            {
                if (obj == null) return null;
                return new KindEntry
                {
                    MaxPayloadKg      = (float?)obj["max_payload_kg"]      ?? 0f,
                    MaxAltitudeM      = (float?)obj["max_altitude_m"]      ?? 0f,
                    InclinationDegMin = (float?)obj["inclination_deg_min"] ?? 80f,
                    InclinationDegMax = (float?)obj["inclination_deg_max"] ?? 100f,
                };
            }
        }

        // bodyName -> (destination_kind -> KindEntry); a missing or null
        // KindEntry means "not unlocked for that (body, kind)".
        private readonly Dictionary<string, Dictionary<string, KindEntry>> _bodies
            = new Dictionary<string, Dictionary<string, KindEntry>>();

        public static ContractEnvelope Empty => new ContractEnvelope();

        public KindEntry Get(string bodyName, string kind)
        {
            if (string.IsNullOrEmpty(bodyName) || string.IsNullOrEmpty(kind)) return null;
            if (!_bodies.TryGetValue(bodyName, out var kinds)) return null;
            return kinds.TryGetValue(kind, out var entry) ? entry : null;
        }

        public bool Has(string bodyName, string kind) => Get(bodyName, kind) != null;

        /// <summary>
        /// Enumerate every (body, kind) pair with a non-null entry. Used
        /// by procedural contract subclasses to pick a random valid target
        /// in Generate.
        /// </summary>
        public IEnumerable<KeyValuePair<string, KeyValuePair<string, KindEntry>>>
            EnumerateValidPairs()
        {
            foreach (var b in _bodies)
            {
                foreach (var k in b.Value)
                {
                    if (k.Value == null) continue;
                    yield return new KeyValuePair<string, KeyValuePair<string, KindEntry>>(
                        b.Key,
                        new KeyValuePair<string, KindEntry>(k.Key, k.Value));
                }
            }
        }

        /// <summary>True iff at least one (body, kind) pair is non-null AND the kind matches the filter.</summary>
        public bool HasAnyValidKind(IEnumerable<string> allowedKinds)
        {
            var set = new HashSet<string>(allowedKinds);
            foreach (var p in EnumerateValidPairs())
                if (set.Contains(p.Value.Key)) return true;
            return false;
        }

        public static ContractEnvelope Parse(JToken token)
        {
            var env = new ContractEnvelope();
            if (token == null || token.Type == JTokenType.Null) return env;
            if (token.Type != JTokenType.Object)
                throw new FormatException(
                    $"contract_envelope must be an object, got {token.Type}");

            foreach (var bodyEntry in (JObject)token)
            {
                string bodyName = bodyEntry.Key;
                if (!(bodyEntry.Value is JObject kindsObj))
                {
                    Debug.LogWarning(
                        $"[KSP-AP] envelope body '{bodyName}' has no kinds object — skipping");
                    continue;
                }
                var kinds = new Dictionary<string, KindEntry>();
                foreach (var kindEntry in kindsObj)
                {
                    JObject kindParams = kindEntry.Value as JObject;
                    kinds[kindEntry.Key] = KindEntry.Parse(kindParams);
                }
                env._bodies[bodyName] = kinds;
            }
            return env;
        }
    }
}
