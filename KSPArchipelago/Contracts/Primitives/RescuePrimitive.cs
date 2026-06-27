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
    /// Kerbal in orbit of the body and completes when they are recovered. This is
    /// the only primitive that creates world state rather than watching the
    /// player's vessel — the free-seat requirement rides as a separate
    /// has_any_part objective emitted by the generator.
    /// </summary>
    public sealed class RescuePrimitive : ContractPrimitiveBase<string>
    {
        public override string Kind => "rescue";

        protected override string Parse(JObject spec)
        {
            string body = (string)spec["body"];
            if (string.IsNullOrEmpty(body))
                throw new FormatException("rescue primitive missing 'body'");
            return body;
        }

        protected override ContractParameter BuildFrom(string body)
            => new RescueKerbalParameter(body);
    }
}
