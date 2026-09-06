using System;

namespace KSPArchipelago.Missions
{
    /// <summary>
    /// A flight-log entry a vessel or kerbal actually recorded at a body.
    ///
    /// THIS IS NOT AN ORDERED SCALE. Do not add a comparison operator, an IComparable,
    /// an int backing you can &gt;=, or an "at least this deep" helper. Two independent
    /// reasons, both of which look like missed optimisations:
    ///
    ///   1. The difficulty ordering inverts with direction of travel. Outbound from home
    ///      it is suborbital &lt; flyby &lt; orbit &lt; surface; arriving from outside the
    ///      system it is flyby &lt; orbit &lt; suborbital &lt; surface, because suborbital sits
    ///      on the far side of orbital capture on the way down.
    ///   2. Deeper does not imply shallower. A direct-entry landing reaches the surface
    ///      without ever orbiting, so Surface must not satisfy an Orbit requirement.
    ///
    /// Every check asks whether the one entry it needs is present in the log's set.
    /// Stock does the same — see ReturnFrom's independent HasEntry() tests.
    /// </summary>
    public enum MissionAchievement { Suborbital, Flyby, Orbit, Surface }

    /// <summary>
    /// The ONE place that maps <see cref="MissionAchievement"/> onto KSP's
    /// <c>FlightLog.EntryType</c> and onto the wire strings the AP server sends
    /// (the schema-7 <c>achievement</c> vocabulary, shared by the
    /// <c>returned_from</c> and <c>tourist</c> primitives). Nothing else in the
    /// mod may spell either alphabet: adding a tier means adding one row to
    /// <see cref="_rows"/> and nothing else.
    /// </summary>
    public static class AchievementVocabulary
    {
        private sealed class Row
        {
            public readonly MissionAchievement Achievement;
            public readonly FlightLog.EntryType EntryType;
            /// <summary>
            /// <c>FlightLog.Entry.type</c> is a string, not the enum
            /// (Assembly-CSharp decompiled :156116), so the entry-type name is
            /// cached once here rather than re-derived for every log entry of
            /// every milestone.
            /// </summary>
            public readonly string EntryTypeName;
            public readonly string Wire;

            public Row(MissionAchievement achievement, FlightLog.EntryType entryType, string wire)
            {
                Achievement = achievement;
                EntryType = entryType;
                EntryTypeName = entryType.ToString();
                Wire = wire;
            }
        }

        private static readonly Row[] _rows =
        {
            new Row(MissionAchievement.Suborbital, FlightLog.EntryType.Suborbit, "suborbital"),
            new Row(MissionAchievement.Flyby,      FlightLog.EntryType.Flyby,    "flyby"),
            new Row(MissionAchievement.Orbit,      FlightLog.EntryType.Orbit,    "orbit"),
            new Row(MissionAchievement.Surface,    FlightLog.EntryType.Land,     "surface"),
        };

        /// <summary>
        /// Wire string (case-insensitive) to achievement. Throws
        /// <see cref="FormatException"/> on anything outside the vocabulary —
        /// callers treat that as schema drift and never offer the contract.
        /// </summary>
        public static MissionAchievement Parse(string wire)
        {
            string s = (wire ?? "").ToLowerInvariant();
            for (int i = 0; i < _rows.Length; i++)
                if (_rows[i].Wire == s) return _rows[i].Achievement;
            throw new FormatException(
                $"unknown achievement '{wire}' "
                + "(expected suborbital, flyby, orbit or surface)");
        }

        /// <summary>The stock flight-log entry type that records this achievement.</summary>
        public static FlightLog.EntryType ToEntryType(MissionAchievement achievement)
        {
            for (int i = 0; i < _rows.Length; i++)
                if (_rows[i].Achievement == achievement) return _rows[i].EntryType;
            throw new FormatException(
                $"achievement '{achievement}' has no FlightLog.EntryType");
        }

        /// <summary>
        /// A raw <c>FlightLog.Entry.type</c> string to an achievement. Returns
        /// false for every entry type outside this vocabulary (Flight, Escape,
        /// Launch, BoardVessel, PlantFlag, …), which the return family ignores.
        /// </summary>
        public static bool TryFromLogEntry(string entryType, out MissionAchievement achievement)
        {
            for (int i = 0; i < _rows.Length; i++)
            {
                if (_rows[i].EntryTypeName == entryType)
                {
                    achievement = _rows[i].Achievement;
                    return true;
                }
            }
            achievement = MissionAchievement.Suborbital;
            return false;
        }
    }
}
