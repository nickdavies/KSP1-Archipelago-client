using System.Collections.Generic;
using UnityEngine;

namespace KSPArchipelago.Missions
{
    /// <summary>
    /// The ONLY <c>GameEvents</c> subscriber for the return/sample family.
    /// It turns the four stock "the craft is home" signals into one
    /// <see cref="FlightMilestone"/> each, snapshots the evidence
    /// (<see cref="MissionEvidence"/>) while the vessel is still in hand, and
    /// publishes on <see cref="MissionEvidence.Observed"/>.
    ///
    /// One subscriber, one snapshot, many consumers: AP location reporting and
    /// the return/sample contract parameters both read the published milestone,
    /// so they cannot drift apart the way independently-hooked detectors did.
    ///
    /// The handlers are INSTANCE methods on purpose. KSP's <c>GameEvents</c>
    /// wraps every subscriber in an <c>EvtDelegate</c> whose constructor reads
    /// the delegate's target object for its originator record; a static method
    /// has no target, so <c>Add</c> throws NullReferenceException before the
    /// hook is installed. <see cref="Register"/> / <see cref="Unregister"/> own
    /// the one instance and are driven from MissionTracker's own event
    /// registration, so the hooks live exactly as long as AP detection does.
    /// </summary>
    public sealed class FlightMilestoneSource
    {
        private static FlightMilestoneSource _instance;

        private FlightMilestoneSource() { }

        /// <summary>
        /// True for player-created vessels (ships, probes, EVA kerbals, etc.).
        /// False for asteroids, debris, and flags. The one copy of this filter.
        /// </summary>
        internal static bool IsMissionVessel(Vessel v)
        {
            if (v == null) return false;
            return v.vesselType != VesselType.SpaceObject &&
                   v.vesselType != VesselType.Flag &&
                   v.vesselType != VesselType.Debris;
        }

        /// <summary>Recovery hands us a ProtoVessel, which carries the same vesselType.</summary>
        internal static bool IsMissionVessel(ProtoVessel v)
        {
            if (v == null) return false;
            return v.vesselType != VesselType.SpaceObject &&
                   v.vesselType != VesselType.Flag &&
                   v.vesselType != VesselType.Debris;
        }

        public static void Register()
        {
            if (_instance != null) return;
            var source = new FlightMilestoneSource();
            source.Hook();
            _instance = source;
        }

        public static void Unregister()
        {
            if (_instance == null) return;
            _instance.Unhook();
            _instance = null;
        }

        private void Hook()
        {
            GameEvents.onVesselRecovered.Add(
                new EventData<ProtoVessel, bool>.OnEvent(OnVesselRecovered));
            GameEvents.VesselSituation.onReturnFromOrbit.Add(
                new EventData<Vessel, CelestialBody>.OnEvent(OnReturnedHome));
            GameEvents.VesselSituation.onReturnFromSurface.Add(
                new EventData<Vessel, CelestialBody>.OnEvent(OnReturnedHome));
            GameEvents.VesselSituation.onLand.Add(
                new EventData<Vessel, CelestialBody>.OnEvent(OnLand));
            GameEvents.onVesselSituationChange.Add(
                new EventData<GameEvents.HostedFromToAction<Vessel, Vessel.Situations>>.OnEvent(OnSituationChange));
        }

        private void Unhook()
        {
            GameEvents.onVesselRecovered.Remove(
                new EventData<ProtoVessel, bool>.OnEvent(OnVesselRecovered));
            GameEvents.VesselSituation.onReturnFromOrbit.Remove(
                new EventData<Vessel, CelestialBody>.OnEvent(OnReturnedHome));
            GameEvents.VesselSituation.onReturnFromSurface.Remove(
                new EventData<Vessel, CelestialBody>.OnEvent(OnReturnedHome));
            GameEvents.VesselSituation.onLand.Remove(
                new EventData<Vessel, CelestialBody>.OnEvent(OnLand));
            GameEvents.onVesselSituationChange.Remove(
                new EventData<GameEvents.HostedFromToAction<Vessel, Vessel.Situations>>.OnEvent(OnSituationChange));
        }

        // Recovery only ever happens on the home body, so this is unconditional.
        // It is also the one signal that survives a save/reload and the Tracking
        // Station: the evidence comes off the part snapshots, not from a
        // runtime set that a scene change would drop.
        private void OnVesselRecovered(ProtoVessel vessel, bool quick)
        {
            if (!IsMissionVessel(vessel)) return;
            Publish(FlightMilestoneKind.Recovered,
                    KSPArchipelagoMod.StartingBody,
                    vessel.GetVesselCrew()?.Count ?? 0,
                    MissionEvidence.SurfaceSampleBodies(vessel),
                    MissionEvidence.AchievementsByBody(vessel));
        }

        // Stock fires onReturnFromOrbit for BOTH ReturnFrom.FlyBy and
        // ReturnFrom.Orbit, and onReturnFromSurface for ReturnFrom.Surface
        // (Assembly-CSharp decompiled :845625, :845674, :845705) — which is why
        // the tier can never be read off the event. `body` is the REMOTE body;
        // the vessel is at home. What it proved comes from the trip log.
        private void OnReturnedHome(Vessel vessel, CelestialBody body)
        {
            if (!IsMissionVessel(vessel)) return;
            Publish(FlightMilestoneKind.ReturnedHome,
                    body != null ? body.name : "",
                    vessel.GetCrewCount(),
                    MissionEvidence.SurfaceSampleBodies(vessel),
                    MissionEvidence.AchievementsByBody(vessel));
        }

        private void OnLand(Vessel vessel, CelestialBody body)
        {
            if (body == null || body.name != KSPArchipelagoMod.StartingBody) return;
            PublishHomeTouchdown(vessel);
        }

        // onLand does not fire for every splashdown path, and an ocean touchdown
        // is a landing in the generator's model, so the SPLASHED transition is
        // the second home-touchdown trigger.
        private void OnSituationChange(
            GameEvents.HostedFromToAction<Vessel, Vessel.Situations> data)
        {
            if (data.to != Vessel.Situations.SPLASHED) return;
            Vessel v = data.host;
            if (v == null || v.mainBody == null) return;
            if (v.mainBody.name != KSPArchipelagoMod.StartingBody) return;
            PublishHomeTouchdown(v);
        }

        private void PublishHomeTouchdown(Vessel vessel)
        {
            if (!IsMissionVessel(vessel)) return;
            Publish(FlightMilestoneKind.HomeTouchdown,
                    KSPArchipelagoMod.StartingBody,
                    vessel.GetCrewCount(),
                    MissionEvidence.SurfaceSampleBodies(vessel),
                    MissionEvidence.AchievementsByBody(vessel));
        }

        private static void Publish(
            FlightMilestoneKind kind, string bodyName, int crewCount,
            HashSet<string> sampleBodies,
            Dictionary<string, HashSet<MissionAchievement>> achievements)
        {
            var milestone = new FlightMilestone(kind, bodyName, crewCount,
                                                sampleBodies, achievements);
            Debug.Log($"[KSP-AP] Milestone {kind} at '{bodyName}': crew={crewCount}, "
                    + $"samples={sampleBodies.Count}, logged bodies={achievements.Count}");
            MissionEvidence.Publish(milestone);
        }
    }
}
