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
        /// Thread-safe check that the spec carries every field this primitive
        /// needs, throwing FormatException if not. Runs at CONNECT (on a worker
        /// thread) so a seed with an unusable parameter is rejected up front
        /// rather than silently failing to offer the contract deep in the run.
        /// Must NOT touch Unity APIs (FlightGlobals, PartLoader, …) — those run
        /// only on the main thread in <see cref="Build"/>. Field presence and
        /// enum-string validity only; body/part existence is checked at build.
        /// </summary>
        void Validate(JObject spec);

        /// <summary>
        /// Build the KSP parameter from a spec object. May throw
        /// FormatException on malformed input — the caller (ApContract.Generate)
        /// converts that to "do not offer this contract", which is fail-safe.
        /// Calls <see cref="Validate"/> first, so it never builds an
        /// already-rejected spec.
        /// </summary>
        ContractParameter Build(JObject spec);
    }
}
