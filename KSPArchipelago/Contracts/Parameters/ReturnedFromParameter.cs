using System;
using Contracts;
using KSPArchipelago.Missions;

namespace KSPArchipelago.Contracts.Parameters
{
    /// <summary>
    /// Completes when a craft comes home carrying a flight log that records
    /// <see cref="Achievement"/> at <see cref="BodyName"/> — the three return
    /// tiers (<c>SOI Return</c> / <c>Orbit Return</c> / <c>Return</c>) as one
    /// parameter, distinguished only by which entry it looks for.
    ///
    /// The entry test is exact and never inferred, matching stock's own
    /// independent <c>HasEntry</c> tests (Assembly-CSharp decompiled
    /// :845598/:845629/:845678) and the reasoning on
    /// <see cref="MissionAchievement"/>: a direct-entry landing satisfies
    /// Surface without ever satisfying Orbit.
    ///
    /// The one substitution is for <see cref="MissionAchievement.Surface"/>: a
    /// recovered surface sample from the body proves the surface was reached
    /// and that the sample got home, which is stronger evidence than the log
    /// entry. It exists because the EVA-bailout craft — a kerbal under personal
    /// chute — carries no trip logger and so records no Land entry.
    ///
    /// The class NAME is the save format (ContractSystem.GetParameterType
    /// matches on Type.Name, Assembly-CSharp decompiled :908720).
    /// </summary>
    public class ReturnedFromParameter : ContractParameter
    {
        /// <summary>Body the flight must have reached. Persisted.</summary>
        public string BodyName = "";

        /// <summary>Which flight-log entry proves it. Persisted by enum name.</summary>
        public MissionAchievement Achievement = MissionAchievement.Surface;

        private bool _eventHooked;

        public ReturnedFromParameter() { }   // KSP deserialization

        public ReturnedFromParameter(string bodyName, MissionAchievement achievement)
        {
            BodyName = bodyName ?? "";
            Achievement = achievement;
        }

        protected override string GetTitle()
        {
            switch (Achievement)
            {
                case MissionAchievement.Flyby:
                    return $"Return home from a flyby of {BodyName}";
                case MissionAchievement.Orbit:
                    return $"Return home from orbit of {BodyName}";
                case MissionAchievement.Surface:
                    return $"Return home from the surface of {BodyName}";
                default:
                    return $"Return home from a suborbital flight over {BodyName}";
            }
        }

        protected override string GetHashString()
            => "ReturnedFrom|" + BodyName + "|" + Achievement;

        protected override void OnRegister()
        {
            MissionEvidence.Observed += OnMilestone;
            _eventHooked = true;
        }

        protected override void OnUnregister()
        {
            if (!_eventHooked) return;
            MissionEvidence.Observed -= OnMilestone;
            _eventHooked = false;
        }

        private void OnMilestone(FlightMilestone milestone)
        {
            if (state == ParameterState.Complete) return;
            if (Root == null || Root.ContractState != Contract.State.Active) return;
            if (string.IsNullOrEmpty(BodyName)) return;

            if (milestone.HasAchievement(BodyName, Achievement))
            {
                SetComplete();
                return;
            }
            if (Achievement == MissionAchievement.Surface
                && milestone.SurfaceSampleBodies.Contains(BodyName))
            {
                SetComplete();
            }
        }

        protected override void OnSave(ConfigNode node)
        {
            node.AddValue("body", BodyName);
            node.AddValue("achievement", Achievement.ToString());
        }

        protected override void OnLoad(ConfigNode node)
        {
            BodyName = node.GetValue("body") ?? "";
            string achievement = node.GetValue("achievement");
            if (!string.IsNullOrEmpty(achievement)
                && Enum.TryParse(achievement, out MissionAchievement parsed))
                Achievement = parsed;
        }
    }
}
