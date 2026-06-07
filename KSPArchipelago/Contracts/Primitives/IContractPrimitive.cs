using Contracts;
using Newtonsoft.Json.Linq;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// A compile-checked factory that turns one declarative parameter spec
    /// (a JObject with a <c>kind</c> field) into a concrete KSP
    /// <see cref="ContractParameter"/>. Each primitive wraps exactly one
    /// verified stock or custom parameter type.
    ///
    /// This interface plus its implementations are the ONLY compile-checked
    /// surface of the contracts bridge — and the codegen target for the
    /// schema source-of-truth. The generator composes these primitives;
    /// recombinations need no client release.
    /// </summary>
    public interface IContractPrimitive
    {
        /// <summary>The slot_data <c>kind</c> string this primitive handles.</summary>
        string Kind { get; }

        /// <summary>
        /// Build the KSP parameter from a spec object. May throw
        /// FormatException on malformed input — the caller (ApContract.Generate)
        /// converts that to "do not offer this contract", which is fail-safe.
        /// </summary>
        ContractParameter Build(JObject spec);
    }
}
