using UnityEngine;

namespace KSPArchipelago.Traps.Actuators
{
    /// <summary>
    /// Timed uniform gravity anomaly: 5-10 seconds of an extra 20-50% of
    /// local gravity, randomly heavier or lighter, applied per rigidbody as
    /// an Acceleration force along the live local gravity vector (uniform
    /// field — no fake torque). Kept SHORT deliberately: sustained anomalous
    /// gravity is a continuous orbit-warping burn, and 15-30s play-tested as
    /// near-unrecoverable. NEVER touches GeeASL/gravParameter: those are
    /// global and save-persisted and would corrupt on-rails orbits. Abort
    /// just stops forcing — the anomaly self-heals.
    /// </summary>
    internal sealed class GravityStormTrap : ITrapActuator
    {
        public string ItemName => "Trap: Gravity Storm";
        public bool ManagesWarpItself => false;
        public bool CanFireWithoutVessel => false;

        public bool IsEligible(Vessel v) => !v.packed;

        public void Fire(MonoBehaviour host, Vessel v, TrapEffectsRunner runner)
        {
            float factor = Random.Range(0.2f, 0.5f) * (Random.value < 0.5f ? -1f : 1f);
            float duration = Random.Range(5f, 10f);
            runner.Add(new GravityEffect(v, factor, duration));
            ScreenMessages.PostScreenMessage(
                factor > 0f
                    ? "<color=orange>TRAP:</color> Gravity surges — everything feels heavy!"
                    : "<color=orange>TRAP:</color> Gravity flutters — everything feels light!",
                5f, ScreenMessageStyle.UPPER_CENTER);
        }

        private sealed class GravityEffect : ITrapEffect
        {
            private readonly Vessel _vessel;
            private readonly float _factor;
            private float _remaining;

            public GravityEffect(Vessel vessel, float factor, float duration)
            {
                _vessel = vessel;
                _factor = factor;
                _remaining = duration;
            }

            public bool Tick(float fixedDeltaTime)
            {
                if (_vessel == null || !_vessel.loaded || _vessel.packed
                    || FlightGlobals.ActiveVessel != _vessel)
                    return false;
                Vector3 extra =
                    (Vector3)FlightGlobals.getGeeForceAtPosition(_vessel.CoM) * _factor;
                foreach (Part p in _vessel.parts)
                    if (p.rb != null)
                        p.rb.AddForce(extra, ForceMode.Acceleration);
                _remaining -= fixedDeltaTime;
                return _remaining > 0f;
            }

            public void Abort() { }   // stopping the force is the whole cleanup
        }
    }
}
