using Contracts;

namespace KSPArchipelago.Contracts.Parameters
{
    /// <summary>
    /// Completes on ANY science from <see cref="BodyName"/> reaching R&amp;D —
    /// recovered or transmitted, from any situation, at any value (a zero-value
    /// repeat of an already-banked subject still counts, because the objective
    /// is "get something back from there", not "bank new science").
    ///
    /// This is the <c>collect_science</c> <c>location:"any"</c> binding. It is
    /// deliberately on the SCIENCE-SUBJECT axis, not the flight-evidence axis:
    /// it asks what data came from a body, which is a different question from
    /// what a vessel proved, so it does not belong to the
    /// <c>MissionEvidence</c> family and keeps its own hooks.
    ///
    /// Both hooks and every filter mirror stock <c>Contracts.Parameters.CollectScience</c>
    /// (Assembly-CSharp decompiled :928162) MINUS its situation test:
    ///   - <c>OnScienceRecieved</c> — skip reverse-engineered science, then
    ///     <c>ScienceSubject.IsFromBody</c> (stock's own body test, :504731 —
    ///     an id <c>Contains("@" + body.name)</c> check; do not hand-parse the id).
    ///   - <c>OnTriggeredDataTransmission</c> — skip aborted transmissions and
    ///     empty data blobs, then match the transmitting vessel's mainBody.
    ///
    /// The class NAME is the save format (ContractSystem.GetParameterType
    /// matches on Type.Name, Assembly-CSharp decompiled :908720).
    /// </summary>
    public class BodyScienceParameter : ContractParameter
    {
        /// <summary>Body the science must come from. Persisted.</summary>
        public string BodyName = "";
        private bool _eventsHooked;

        public BodyScienceParameter() { }   // KSP deserialization

        public BodyScienceParameter(string bodyName)
        {
            BodyName = bodyName ?? "";
        }

        protected override string GetTitle()
            => $"Return or transmit science from {BodyName}";

        protected override string GetHashString()
            => "ApBodyScience|" + BodyName;

        protected override void OnRegister()
        {
            // KSP misspells "Received" as "Recieved".
            GameEvents.OnScienceRecieved.Add(
                new EventData<float, ScienceSubject, ProtoVessel, bool>.OnEvent(OnScience));
            GameEvents.OnTriggeredDataTransmission.Add(
                new EventData<ScienceData, Vessel, bool>.OnEvent(OnTriggeredScience));
            _eventsHooked = true;
        }

        protected override void OnUnregister()
        {
            if (!_eventsHooked) return;
            GameEvents.OnScienceRecieved.Remove(
                new EventData<float, ScienceSubject, ProtoVessel, bool>.OnEvent(OnScience));
            GameEvents.OnTriggeredDataTransmission.Remove(
                new EventData<ScienceData, Vessel, bool>.OnEvent(OnTriggeredScience));
            _eventsHooked = false;
        }

        // Guards shared by both hooks: never complete a parameter whose contract
        // isn't Active (the objective is post-activation by construction).
        private bool Listening()
        {
            if (state == ParameterState.Complete) return false;
            if (Root == null || Root.ContractState != Contract.State.Active) return false;
            return !string.IsNullOrEmpty(BodyName);
        }

        private void OnScience(float science, ScienceSubject subject,
                               ProtoVessel pv, bool reverseEngineered)
        {
            if (reverseEngineered || subject == null) return;
            if (!Listening()) return;
            CelestialBody body = FlightGlobals.GetBodyByName(BodyName);
            if (body == null) return;
            if (!subject.IsFromBody(body)) return;
            SetComplete();
        }

        private void OnTriggeredScience(ScienceData data, Vessel origin, bool xmitAborted)
        {
            if (data == null || origin == null || origin.mainBody == null || xmitAborted) return;
            if (data.dataAmount <= 0f) return;
            if (!Listening()) return;
            CelestialBody body = FlightGlobals.GetBodyByName(BodyName);
            if (body == null) return;
            if (origin.mainBody != body) return;
            SetComplete();
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
