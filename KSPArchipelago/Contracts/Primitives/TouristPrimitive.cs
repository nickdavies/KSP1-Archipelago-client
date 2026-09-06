using System;
using Contracts;
using Newtonsoft.Json.Linq;
using KSPArchipelago.Contracts.Parameters;
using KSPArchipelago.Missions;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind":"tourist", "name":"Bob Kerman", "female":false, "body":"Mun",
    /// "entry":"orbit" }</c>  (entry uses the shared achievement vocabulary:
    /// suborbital | flyby | orbit | surface)
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
            string entry = (string)spec["entry"];
            if (string.IsNullOrEmpty(entry))
                throw new FormatException("tourist primitive missing 'entry'");
            return new Spec
            {
                Name = name,
                Female = female.Value,
                Body = body,
                // The shared achievement vocabulary and its single
                // FlightLog.EntryType mapping — the stock tour parameter this
                // primitive hosts is driven by the entry type, so tourism and
                // the return family name flight-log entries the same way.
                Entry = AchievementVocabulary.ToEntryType(
                    AchievementVocabulary.Parse(entry)),
            };
        }

        protected override ContractParameter BuildFrom(Spec p)
            => new ApTouristParameter(p.Name, p.Female, p.Body, p.Entry);
    }
}
