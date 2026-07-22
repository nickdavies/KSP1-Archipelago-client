using Contracts;
using FinePrint;
using FinePrint.Contracts.Parameters;
using FinePrint.Utilities;
using UnityEngine;

namespace KSPArchipelago.Contracts.Parameters
{
    /// <summary>
    /// Stock <see cref="SurveyWaypointParameter"/> with one adaptation for our
    /// data-driven host. Stock <c>SurveyContract.Generate</c> calls
    /// <c>ProcessWaypoint()</c> immediately AFTER <c>AddParameter</c>
    /// (Assembly-CSharp decompiled :886058); that call is what SUBMITS the
    /// waypoint (sets the param's private <c>submittedWaypoint</c> flag), which
    /// gates BOTH navigation and completion — <c>CheckExperimentResults</c>
    /// early-returns while it is false (:899617). Our generic
    /// <c>ApGenericContract</c> host only calls <c>AddParameter</c>, so nothing
    /// ever submits the waypoint on a fresh force-offer.
    ///
    /// This subclass pumps <c>ProcessWaypoint()</c> from <see cref="OnUpdate"/>
    /// once a flight/track-station scene is up, until the waypoint is registered
    /// with <c>WaypointManager</c>. <c>ProcessWaypoint()</c> early-returns once
    /// submitted (:899913), so the repeated calls are idempotent and cheap.
    /// </summary>
    public class ApSurveyWaypointParameter : SurveyWaypointParameter
    {
        public ApSurveyWaypointParameter() : base() { }

        public ApSurveyWaypointParameter(string experiment, string actionDescription,
                                         CelestialBody targetBody, Waypoint wp, FlightBand band)
            : base(experiment, actionDescription, targetBody, wp, band) { }

        protected override void OnUpdate()
        {
            if (Root != null && Root.ContractState == Contract.State.Active
                && (HighLogic.LoadedSceneIsFlight
                    || HighLogic.LoadedScene == GameScenes.TRACKSTATION)
                && !WaypointSubmitted())
            {
                ProcessWaypoint();
            }
            base.OnUpdate();
        }

        // Stock's submittedWaypoint is private; detect submission by the waypoint
        // being present in the manager (ProcessWaypoint adds it there at the same
        // time it sets submittedWaypoint).
        private bool WaypointSubmitted()
        {
            WaypointManager mgr = WaypointManager.Instance();
            return mgr != null && wp != null && mgr.Waypoints.Contains(wp);
        }
    }
}
