using System;
using System.Collections.Generic;
using Contracts;
using Newtonsoft.Json.Linq;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// Registry of contract primitives keyed by <c>kind</c>, plus the schema
    /// version handshake. The server only emits kinds a client version
    /// advertises; the client rejects unknown <c>schema</c>/<c>kind</c> at
    /// build time. This is the runtime half of the client/server drift
    /// mitigation (version handshake + whitelist).
    ///
    /// Schema 4 primitives: <c>situation</c>, <c>resource</c>, <c>has_any_part</c>,
    /// <c>crew_capacity</c>, <c>plant_flag</c>, <c>sample_return</c>, <c>has_system</c>,
    /// <c>specific_orbit</c>, <c>collect_science</c>, <c>rescue</c>. (Schema 3
    /// changed the wire format: a single <c>location</c> became a <c>locations</c>
    /// array. Schema 4 added the <c>rescue</c> primitive, which spawns a stranded
    /// Kerbal — the only world-mutating primitive.)
    /// </summary>
    public static class ContractPrimitiveRegistry
    {
        /// <summary>Highest primitive-registry schema this client understands.</summary>
        public const int Schema = 4;

        private static readonly Dictionary<string, IContractPrimitive> _byKind
            = new Dictionary<string, IContractPrimitive>(StringComparer.Ordinal);

        static ContractPrimitiveRegistry()
        {
            Register(new SituationPrimitive());
            Register(new ResourcePrimitive());
            Register(new HasAnyPartPrimitive());
            Register(new CrewCapacityPrimitive());
            Register(new PlantFlagPrimitive());
            Register(new SampleReturnPrimitive());
            Register(new HasSystemPrimitive());
            Register(new SpecificOrbitPrimitive());
            Register(new CollectSciencePrimitive());
            Register(new RescuePrimitive());
        }

        private static void Register(IContractPrimitive primitive)
            => _byKind[primitive.Kind] = primitive;

        /// <summary>
        /// Reject contract specs the client can't safely actuate. Throws
        /// FormatException on an unsupported schema — callers treat that as
        /// "do not offer", which is fail-safe (a contract that can never be
        /// completed is worse than one that is never offered).
        /// </summary>
        public static void ValidateSchema(int schema)
        {
            if (schema != Schema)
                throw new FormatException(
                    $"contract schema {schema} unsupported (client supports {Schema}); "
                    + "update the client mod to a version matching the generator.");
        }

        public static bool Supports(string kind)
            => !string.IsNullOrEmpty(kind) && _byKind.ContainsKey(kind);

        /// <summary>
        /// Connect-time drift check for one contract: reject an unsupported
        /// schema or any unknown primitive kind up front, so the player gets a
        /// clear "update the mod" error instead of a silently un-offered
        /// contract (which would strand its AP location). Throws FormatException
        /// on drift — HandleConnect converts that to a connection rejection.
        /// </summary>
        public static void ValidateSpec(ContractSlotSpec spec)
        {
            ValidateSchema(spec.Schema);
            foreach (JObject paramSpec in spec.Parameters)
            {
                string kind = (string)paramSpec["kind"];
                if (!Supports(kind))
                    throw new FormatException(
                        $"contract '{spec.Location}' uses unknown primitive kind "
                        + $"'{kind}' (client schema {Schema}); update the client mod.");
            }
        }

        /// <summary>Build one parameter from its spec, dispatching on <c>kind</c>.</summary>
        public static ContractParameter Build(JObject spec)
        {
            if (spec == null) throw new FormatException("null contract parameter spec");
            string kind = (string)spec["kind"];
            if (string.IsNullOrEmpty(kind))
                throw new FormatException("contract parameter missing 'kind'");
            if (!_byKind.TryGetValue(kind, out IContractPrimitive primitive))
                throw new FormatException(
                    $"unknown contract primitive kind '{kind}' (client schema {Schema})");
            return primitive.Build(spec);
        }
    }
}
