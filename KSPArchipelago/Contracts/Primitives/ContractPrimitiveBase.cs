using Contracts;
using Newtonsoft.Json.Linq;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// Base for primitives that makes connect-time validation and the real build
    /// run the EXACT same field-parsing, so they cannot drift apart:
    ///   - <see cref="Parse"/> is the SINGLE place that reads the JObject
    ///     (thread-safe, no Unity APIs; throws FormatException on any missing or
    ///     invalid field). It is the source of truth for "what this primitive
    ///     requires".
    ///   - <see cref="IContractPrimitive.Validate"/> just runs Parse and discards
    ///     — the connect-time check.
    ///   - <see cref="IContractPrimitive.Build"/> runs the SAME Parse, then hands
    ///     the result to <see cref="BuildFrom"/> for the main-thread Unity
    ///     construction. BuildFrom never receives the JObject, so it is
    ///     compile-time impossible for Build to depend on a field that Parse
    ///     (and therefore Validate) didn't already require.
    /// Validate/Build are sealed here; a subclass supplies only Kind, Parse and
    /// BuildFrom and cannot reintroduce drift by overriding them.
    /// </summary>
    public abstract class ContractPrimitiveBase<TParsed> : IContractPrimitive
    {
        public abstract string Kind { get; }

        /// <summary>Thread-safe field parse + validation. The ONLY JObject reader.</summary>
        protected abstract TParsed Parse(JObject spec);

        /// <summary>Main-thread construction from the parsed fields. Gets no JObject.</summary>
        protected abstract ContractParameter BuildFrom(TParsed parsed);

        public void Validate(JObject spec) { Parse(spec); }

        public ContractParameter Build(JObject spec) { return BuildFrom(Parse(spec)); }
    }
}
