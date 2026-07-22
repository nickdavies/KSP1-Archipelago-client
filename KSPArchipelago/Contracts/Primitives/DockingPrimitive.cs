using System;
using Contracts;
using Newtonsoft.Json.Linq;
using KSPArchipelago.Contracts.Parameters;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind": "docking", "body": "Mun" }</c>
    ///
    /// Builds an <see cref="ApDockingParameter"/>: dock two separately-launched
    /// craft in the target body's sphere of influence. Detection is event-based
    /// (<c>GameEvents.onPartCouple</c> with stock KSPAchievements.Docking's
    /// filters), so it is repeatable and post-activation by construction — a dock
    /// only counts if performed while the contract is Active. Body existence is
    /// verified at build (fail-safe: an unknown body → contract never offered).
    /// </summary>
    public sealed class DockingPrimitive : ContractPrimitiveBase<string>
    {
        public override string Kind => "docking";

        protected override string Parse(JObject spec)
        {
            string body = (string)spec["body"];
            if (string.IsNullOrEmpty(body))
                throw new FormatException("docking primitive missing 'body'");
            return body;
        }

        protected override ContractParameter BuildFrom(string bodyName)
        {
            if (FlightGlobals.GetBodyByName(bodyName) == null)
                throw new FormatException($"docking primitive: unknown body '{bodyName}'");
            return new ApDockingParameter(bodyName);
        }
    }
}
