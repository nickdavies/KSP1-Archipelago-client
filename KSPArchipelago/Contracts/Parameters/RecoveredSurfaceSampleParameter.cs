using Contracts;
using KSPArchipelago.Missions;

namespace KSPArchipelago.Contracts.Parameters
{
    /// <summary>
    /// Completes when a surface SAMPLE taken on <see cref="BodyName"/>
    /// physically comes home — the sample is aboard a craft that landed at, or
    /// was recovered on, the home body. Transmitted science never counts (a
    /// transmission leaves the sample where it was), and there is no crew check:
    /// an uncrewed sample-return probe satisfies it exactly as a kerbal does.
    ///
    /// Evidence comes from <see cref="MissionEvidence.Observed"/>, the same
    /// milestone MissionTracker awards <c>{B} Sample Return</c> from, so this
    /// objective and its AP location can never disagree. That also covers the
    /// EVA-bailout shape, where the sample rides home on a kerbal whose
    /// "vessel" has no flight log at all.
    ///
    /// Subscribing in OnRegister (fired when the contract goes Active) is what
    /// makes the objective post-activation: a sample recovered before the
    /// contract was offered cannot satisfy it.
    ///
    /// The class NAME is the save format. ContractSystem.GetParameterType
    /// restores parameters by Type.Name (Assembly-CSharp decompiled :908720),
    /// so renaming this type silently drops the objective from in-progress saves.
    /// </summary>
    public class RecoveredSurfaceSampleParameter : ContractParameter
    {
        /// <summary>Body whose surface sample must come home. Persisted.</summary>
        public string BodyName = "";
        private bool _eventHooked;

        public RecoveredSurfaceSampleParameter() { }   // KSP deserialization

        public RecoveredSurfaceSampleParameter(string bodyName)
        {
            BodyName = bodyName ?? "";
        }

        protected override string GetTitle()
            => $"Recover a surface sample from {BodyName}";

        protected override string GetHashString()
            => "RecoveredSurfaceSample|" + BodyName;

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
            if (milestone.SurfaceSampleBodies.Contains(BodyName)) SetComplete();
        }

        protected override void OnSave(ConfigNode node)
        {
            node.AddValue("body", BodyName);
        }

        protected override void OnLoad(ConfigNode node)
        {
            BodyName = node.GetValue("body") ?? "";
        }
    }
}
