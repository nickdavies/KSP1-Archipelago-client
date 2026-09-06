using System.Collections.Generic;

namespace KSPArchipelago.Missions
{
    /// <summary>
    /// Which stock signal produced a <see cref="FlightMilestone"/>.
    ///
    /// Every kind means the same thing — "this vessel is now at home". Recovery
    /// only ever happens on the home body, the stock <c>onReturnFrom*</c> events
    /// fire on home arrival (Assembly-CSharp decompiled :845625/:845674/:845705,
    /// reached only via <c>mainBody.isHomeWorld</c> at :845568), and a
    /// HomeTouchdown is a landing or splashdown at the home body. Consumers
    /// therefore never have to ask "did it get home" — only "what did it prove".
    /// </summary>
    public enum FlightMilestoneKind
    {
        /// <summary><c>onVesselRecovered</c> — the craft was recovered.</summary>
        Recovered,

        /// <summary>
        /// <c>onReturnFromSurface</c> / <c>onReturnFromOrbit</c> — the craft
        /// arrived home having been to another body.
        /// </summary>
        ReturnedHome,

        /// <summary><c>onLand</c>, or a SPLASHED transition, at the home body.</summary>
        HomeTouchdown,
    }

    /// <summary>
    /// One immutable "a vessel came home" record: everything the return/sample
    /// family needs to decide what that flight proved, gathered once at the
    /// moment the stock event fired.
    ///
    /// The evidence fields are read-only by convention (net40 has no
    /// IReadOnly* collections) — <see cref="FlightMilestoneSource"/> builds them
    /// fresh per milestone and hands them out; no consumer may mutate them.
    /// </summary>
    public sealed class FlightMilestone
    {
        public readonly FlightMilestoneKind Kind;

        /// <summary>
        /// The body the triggering stock event named — the remote body for
        /// <see cref="FlightMilestoneKind.ReturnedHome"/>, the home body
        /// otherwise. Diagnostic context only: every award and every contract
        /// parameter decides from <see cref="AchievementsByBody"/> and
        /// <see cref="SurfaceSampleBodies"/>, which are the actual evidence.
        /// </summary>
        public readonly string BodyName;

        /// <summary>Crew aboard at the moment the milestone fired.</summary>
        public readonly int CrewCount;

        /// <summary>
        /// Bodies whose surface SAMPLE is physically aboard. Never null.
        /// A sample here proves both "reached that surface" and "came home",
        /// which is why it substitutes for a missing flight-log Land entry.
        /// </summary>
        public readonly HashSet<string> SurfaceSampleBodies;

        /// <summary>
        /// Body name to the SET of flight-log entries its logs actually hold.
        /// Never null, never a maximum — see <see cref="MissionAchievement"/>
        /// for why a set is the only correct shape.
        /// </summary>
        public readonly Dictionary<string, HashSet<MissionAchievement>> AchievementsByBody;

        public FlightMilestone(
            FlightMilestoneKind kind,
            string bodyName,
            int crewCount,
            HashSet<string> surfaceSampleBodies,
            Dictionary<string, HashSet<MissionAchievement>> achievementsByBody)
        {
            Kind = kind;
            BodyName = bodyName ?? "";
            CrewCount = crewCount;
            SurfaceSampleBodies = surfaceSampleBodies ?? new HashSet<string>();
            AchievementsByBody = achievementsByBody
                ?? new Dictionary<string, HashSet<MissionAchievement>>();
        }

        /// <summary>
        /// True iff this body's log set contains exactly this entry. Never
        /// infers one tier from another — see <see cref="MissionAchievement"/>.
        /// </summary>
        public bool HasAchievement(string bodyName, MissionAchievement achievement)
        {
            HashSet<MissionAchievement> set;
            return !string.IsNullOrEmpty(bodyName)
                && AchievementsByBody.TryGetValue(bodyName, out set)
                && set.Contains(achievement);
        }
    }
}
