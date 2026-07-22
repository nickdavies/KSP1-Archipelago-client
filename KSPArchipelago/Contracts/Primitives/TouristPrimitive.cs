using System;
using Contracts;
using Newtonsoft.Json.Linq;
using KSPArchipelago.Contracts.Parameters;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind":"tourist", "name":"Bob Kerman", "female":false, "body":"Mun",
    /// "entry":"Orbit" }</c>  (entry ∈ {Suborbit, Orbit})
    ///
    /// Builds an <see cref="ApTouristParameter"/>, which resolves-or-creates a
    /// <c>KerbalType.Tourist</c> for the seeded name+gender and hosts the stock
    /// tour objective (<c>KerbalTourParameter</c> + <c>KerbalDestinationParameter</c>
    /// for the given body and flight-log entry type). The actual roster name is
    /// persisted so reloads reuse it; the whole build runs main-thread at
    /// force-offer. All four fields are required — a missing/invalid value is
    /// rejected as schema drift (fail-safe: the contract is never offered).
    /// </summary>
    public sealed class TouristPrimitive : ContractPrimitiveBase<TouristPrimitive.Spec>
    {
        public override string Kind => "tourist";

        public struct Spec
        {
            public string Name;
            public bool Female;
            public string Body;
            public FlightLog.EntryType Entry;
        }

        protected override Spec Parse(JObject spec)
        {
            string name = (string)spec["name"];
            if (string.IsNullOrEmpty(name))
                throw new FormatException("tourist primitive missing 'name'");
            bool? female = spec.Value<bool?>("female");
            if (female == null)
                throw new FormatException("tourist primitive missing 'female'");
            string body = (string)spec["body"];
            if (string.IsNullOrEmpty(body))
                throw new FormatException("tourist primitive missing 'body'");
            return new Spec
            {
                Name = name,
                Female = female.Value,
                Body = body,
                Entry = ParseEntry((string)spec["entry"]),
            };
        }

        protected override ContractParameter BuildFrom(Spec p)
            => new ApTouristParameter(p.Name, p.Female, p.Body, p.Entry);

        private static FlightLog.EntryType ParseEntry(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "suborbit":
                case "suborbital": return FlightLog.EntryType.Suborbit;
                case "orbit":
                case "orbiting":   return FlightLog.EntryType.Orbit;
                default:
                    throw new FormatException(
                        $"tourist primitive: unknown entry '{s}' "
                        + "(expected Suborbit or Orbit)");
            }
        }
    }
}
