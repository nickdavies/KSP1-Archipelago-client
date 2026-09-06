using System;
using System.Collections.Generic;

namespace KSPArchipelago.Missions
{
    /// <summary>
    /// Pure readers over "what did this vessel or kerbal actually prove", plus
    /// the single event every consumer of that evidence listens to.
    ///
    /// Nothing here subscribes to <c>GameEvents</c> — that is
    /// <see cref="FlightMilestoneSource"/>'s job, and it is the only subscriber
    /// for the return/sample family. These methods are side-effect-free
    /// snapshot readers, which is what makes them testable and what makes the
    /// evidence identical for the AP location reporter and for contract
    /// parameters: they read the same call.
    /// </summary>
    public static class MissionEvidence
    {
        // ------------------------------------------------------------------
        // Surface samples
        // ------------------------------------------------------------------

        private const string SurfaceSamplePrefix = "surfaceSample@";

        /// <summary>
        /// Extracts the body name from a surface-sample science subject id.
        /// Format: <c>surfaceSample@{Body}Srf{Landed|Splashed}{Biome}</c>.
        /// Returns null for any other subject. The ONE parser for this form.
        /// </summary>
        public static string ExtractSampleBody(string subjectId)
        {
            if (string.IsNullOrEmpty(subjectId)) return null;
            if (!subjectId.StartsWith(SurfaceSamplePrefix, StringComparison.Ordinal))
                return null;
            int srfIdx = subjectId.IndexOf("Srf", SurfaceSamplePrefix.Length, StringComparison.Ordinal);
            if (srfIdx <= SurfaceSamplePrefix.Length) return null;
            return subjectId.Substring(SurfaceSamplePrefix.Length,
                                       srfIdx - SurfaceSamplePrefix.Length);
        }

        /// <summary>
        /// Body names with surface-sample data stored on a ProtoVessel — the
        /// recovery path, where the craft exists only as part snapshots.
        /// </summary>
        public static HashSet<string> SurfaceSampleBodies(ProtoVessel vessel)
        {
            var bodies = new HashSet<string>();
            if (vessel == null || vessel.protoPartSnapshots == null) return bodies;
            foreach (ProtoPartSnapshot part in vessel.protoPartSnapshots)
            {
                foreach (ProtoPartModuleSnapshot module in part.modules)
                {
                    if (module.moduleName != "ModuleScienceExperiment" &&
                        module.moduleName != "ModuleScienceContainer")
                        continue;
                    foreach (ConfigNode dataNode in module.moduleValues.GetNodes("ScienceData"))
                    {
                        string body = ExtractSampleBody(dataNode.GetValue("subjectID"));
                        if (body != null) bodies.Add(body);
                    }
                }
            }
            return bodies;
        }

        /// <summary>Body names with surface-sample data aboard a live Vessel.</summary>
        public static HashSet<string> SurfaceSampleBodies(Vessel vessel)
        {
            var bodies = new HashSet<string>();
            if (vessel == null || vessel.Parts == null) return bodies;
            foreach (Part part in vessel.Parts)
            {
                foreach (PartModule module in part.Modules)
                {
                    IScienceDataContainer container = module as IScienceDataContainer;
                    if (container == null) continue;
                    ScienceData[] data = container.GetData();
                    if (data == null) continue;
                    foreach (ScienceData d in data)
                    {
                        string body = ExtractSampleBody(d.subjectID);
                        if (body != null) bodies.Add(body);
                    }
                }
            }
            return bodies;
        }

        // ------------------------------------------------------------------
        // Flight-log achievements
        // ------------------------------------------------------------------

        /// <summary>
        /// What a live vessel's logs record, per body: the union of the vessel
        /// trip log (merged from every <c>ModuleTripLogger</c> aboard) and each
        /// crew member's CURRENT-flight log.
        ///
        /// The crew half is not redundant. A craft carrying no
        /// <c>ModuleTripLogger</c> has an empty trip log, and the EVA-bailout
        /// case — a lone kerbal descending under personal chute — is a "vessel"
        /// that never had one. Crew logs cover both.
        ///
        /// <c>flightLog</c>, never <c>careerLog</c>: careerLog spans previous
        /// flights (ProtoCrewMember :163917/:163919), so a kerbal who flew to
        /// Duna last mission would keep proving Duna on every flight after.
        /// </summary>
        public static Dictionary<string, HashSet<MissionAchievement>> AchievementsByBody(Vessel vessel)
        {
            var result = NewMap();
            if (vessel == null) return result;
            MergeLog(result, VesselTripLog.FromVessel(vessel).Log);
            MergeCrewLogs(result, vessel.GetVesselCrew());
            return result;
        }

        /// <summary>
        /// What a recovered vessel's logs record, per body.
        ///
        /// <c>ProtoVessel</c> has no flight-log field, so the trip log is
        /// rebuilt from the <c>ModuleTripLogger</c> part snapshots by stock's
        /// own <c>VesselTripLog.FromProtoVessel</c> (Assembly-CSharp decompiled
        /// :411729 — it loads each snapshot's "Log" node into a fresh FlightLog
        /// and merges). Recovery from the Tracking Station has no live vessel at
        /// all; recovery from flight does, and its in-memory modules can hold
        /// entries the snapshot predates, so the live vessel is merged in too
        /// whenever <c>vesselRef</c> is present.
        ///
        /// The crew logs are best-effort HERE and nowhere else. Stock's
        /// <c>VesselRecovery</c> subscribes to <c>onVesselRecovered</c> from
        /// <c>OnAwake</c> (Assembly-CSharp decompiled :508327) long before the
        /// mod connects, so it runs first, and its <c>ArchiveFlightLog</c>
        /// (:508808 into :167174) has already emptied every kerbal's
        /// <c>flightLog</c> into <c>careerLog</c> by the time we are called. The
        /// trip log is untouched by that, and the crew evidence for the same
        /// flight already arrived on the HomeTouchdown / ReturnedHome milestone,
        /// which fires on the landing that necessarily precedes a recovery.
        /// </summary>
        public static Dictionary<string, HashSet<MissionAchievement>> AchievementsByBody(ProtoVessel vessel)
        {
            var result = NewMap();
            if (vessel == null) return result;
            MergeLog(result, VesselTripLog.FromProtoVessel(vessel).Log);
            if (vessel.vesselRef != null)
                MergeLog(result, VesselTripLog.FromVessel(vessel.vesselRef).Log);
            MergeCrewLogs(result, vessel.GetVesselCrew());
            return result;
        }

        /// <summary>
        /// What one kerbal's CURRENT flight recorded, per body. Same
        /// <c>flightLog</c>-not-<c>careerLog</c> rule as the vessel readers.
        /// </summary>
        public static Dictionary<string, HashSet<MissionAchievement>> AchievementsByBody(ProtoCrewMember crew)
        {
            var result = NewMap();
            if (crew == null) return result;
            MergeLog(result, crew.flightLog);
            return result;
        }

        private static Dictionary<string, HashSet<MissionAchievement>> NewMap()
            => new Dictionary<string, HashSet<MissionAchievement>>(StringComparer.Ordinal);

        private static void MergeCrewLogs(
            Dictionary<string, HashSet<MissionAchievement>> into, List<ProtoCrewMember> crew)
        {
            if (crew == null) return;
            for (int i = 0; i < crew.Count; i++)
                if (crew[i] != null) MergeLog(into, crew[i].flightLog);
        }

        // Accumulates one flight log's entries into the per-body sets. Entry
        // types outside the achievement vocabulary (Flight, Escape, Launch,
        // BoardVessel, PlantFlag, …) are skipped, and an entry with no target
        // names no body so it proves nothing.
        private static void MergeLog(
            Dictionary<string, HashSet<MissionAchievement>> into, FlightLog log)
        {
            if (log == null) return;
            List<FlightLog.Entry> entries = log.Entries;
            if (entries == null) return;
            for (int i = 0; i < entries.Count; i++)
            {
                FlightLog.Entry entry = entries[i];
                if (entry == null || string.IsNullOrEmpty(entry.target)) continue;
                MissionAchievement achievement;
                if (!AchievementVocabulary.TryFromLogEntry(entry.type, out achievement)) continue;
                HashSet<MissionAchievement> set;
                if (!into.TryGetValue(entry.target, out set))
                {
                    set = new HashSet<MissionAchievement>();
                    into[entry.target] = set;
                }
                set.Add(achievement);
            }
        }

        // ------------------------------------------------------------------
        // The evidence event
        // ------------------------------------------------------------------

        /// <summary>
        /// Fired once per "a vessel came home" signal, by
        /// <see cref="FlightMilestoneSource"/> and nothing else. Both the AP
        /// location reporter (MissionTracker) and the return/sample contract
        /// parameters subscribe here instead of to <c>GameEvents</c>, so they
        /// can never disagree about what a flight proved.
        /// </summary>
        public static event Action<FlightMilestone> Observed;

        internal static void Publish(FlightMilestone milestone)
        {
            if (milestone == null) return;
            Action<FlightMilestone> handlers = Observed;
            if (handlers != null) handlers(milestone);
        }
    }
}
