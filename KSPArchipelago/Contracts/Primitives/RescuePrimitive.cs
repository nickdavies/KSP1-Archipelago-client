using System;
using Contracts;
using Newtonsoft.Json.Linq;
using KSPArchipelago.Contracts.Parameters;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind": "rescue", "body": "Mun" }</c>
    ///
    /// Builds a <see cref="RescueKerbalParameter"/>, which SPAWNS a stranded
    /// Kerbal in low orbit of the body and completes when they are recovered.
    /// This is the only primitive that creates world state rather than watching
    /// the player's vessel — the free-seat requirement rides as a separate
    /// has_any_part objective emitted by the generator.
    /// </summary>
    public sealed class RescuePrimitive : IContractPrimitive
    {
        public string Kind => "rescue";

        public ContractParameter Build(JObject spec)
        {
            string bodyName = (string)spec["body"];
            if (string.IsNullOrEmpty(bodyName))
                throw new FormatException("rescue primitive missing 'body'");
            return new RescueKerbalParameter(bodyName);
        }
    }
}
