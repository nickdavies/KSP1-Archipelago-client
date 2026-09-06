using System;
using Contracts;
using Newtonsoft.Json.Linq;
using KSPArchipelago.Contracts.Parameters;
using KSPArchipelago.Missions;

namespace KSPArchipelago.Contracts.Primitives
{
    /// <summary>
    /// <c>{ "kind":"returned_from", "body":"Duna", "achievement":"surface" }</c>
    /// (achievement ∈ {suborbital, flyby, orbit, surface})
    ///
    /// Semantics: the craft's flight log records <c>achievement</c> for
    /// <c>body</c>, AND it got home. The three return tiers the generator emits
    /// as separate missions — <c>flyby</c> → SOI Return, <c>orbit</c> → Orbit
    /// Return, <c>surface</c> → Return — all ride this one primitive.
    ///
    /// The achievement is NOT a threshold: <c>orbit</c> is not satisfied by a
    /// direct-entry landing, and <c>flyby</c> is not implied by an orbit. See
    /// <see cref="MissionAchievement"/> for why inferring either way is wrong.
    /// </summary>
    public sealed class ReturnedFromPrimitive : ContractPrimitiveBase<ReturnedFromPrimitive.Spec>
    {
        public override string Kind => "returned_from";

        public struct Spec
        {
            public string Body;
            public MissionAchievement Achievement;
        }

        protected override Spec Parse(JObject spec)
        {
            string bodyName = (string)spec["body"];
            if (string.IsNullOrEmpty(bodyName))
                throw new FormatException("returned_from primitive missing 'body'");
            JToken achievement = spec["achievement"];
            if (achievement == null || achievement.Type == JTokenType.Null)
                throw new FormatException("returned_from primitive missing 'achievement'");
            return new Spec
            {
                Body = bodyName,
                Achievement = AchievementVocabulary.Parse((string)achievement),
            };
        }

        protected override ContractParameter BuildFrom(Spec p)
        {
            CelestialBody body = FlightGlobals.GetBodyByName(p.Body);
            if (body == null)
                throw new FormatException($"returned_from primitive: unknown body '{p.Body}'");
            return new ReturnedFromParameter(p.Body, p.Achievement);
        }
    }
}
