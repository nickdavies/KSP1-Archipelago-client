using UnityEngine;

namespace KSPArchipelago.Traps.Actuators
{
    /// <summary>
    /// Sustained spin-up: 2-4 seconds of rigid angular acceleration about a
    /// random axis (90-180 deg/s total if unopposed), applied per-rigidbody
    /// as matched angular + linear velocity increments (omega x r about the
    /// CoM) so the craft spins as a unit instead of tearing its joints.
    /// Sustained forcing is the point — a one-shot impulse was a 20-degree
    /// nudge SAS cancelled instantly; this makes SAS fight for the duration
    /// and then recover a real tumble. Never on the ground (wheel and
    /// lander-leg destruction is a pure kill, not a quirk).
    /// </summary>
    internal sealed class SpinCycleTrap : ITrapActuator
    {
        public string ItemName => "Trap: Spin Cycle";
        public bool ManagesWarpItself => false;
        public bool CanFireWithoutVessel => false;

        public bool IsEligible(Vessel v)
            => !v.packed
               && v.situation != Vessel.Situations.LANDED
               && v.situation != Vessel.Situations.SPLASHED
               && v.situation != Vessel.Situations.PRELAUNCH;

        public void Fire(MonoBehaviour host, Vessel v, TrapEffectsRunner runner)
        {
            float duration = Random.Range(2f, 4f);
            Vector3 alpha = Random.onUnitSphere
                * (Random.Range(90f, 180f) * Mathf.Deg2Rad / duration);
            runner.Add(new SpinEffect(v, alpha, duration));
            ScreenMessages.PostScreenMessage(
                "<color=orange>TRAP:</color> The universe gives your ship a good spin!",
                5f, ScreenMessageStyle.UPPER_CENTER);
        }

        private sealed class SpinEffect : ITrapEffect
        {
            private readonly Vessel _vessel;
            private readonly Vector3 _alpha;   // rad/s^2, world axis
            private float _remaining;

            public SpinEffect(Vessel vessel, Vector3 alpha, float duration)
            {
                _vessel = vessel;
                _alpha = alpha;
                _remaining = duration;
            }

            public bool Tick(float fixedDeltaTime)
            {
                if (_vessel == null || !_vessel.loaded || _vessel.packed
                    || FlightGlobals.ActiveVessel != _vessel)
                    return false;

                Vector3 deltaOmega = _alpha * fixedDeltaTime;
                Vector3 com = _vessel.CoM;
                foreach (Part p in _vessel.parts)
                {
                    Rigidbody rb = p.rb;
                    if (rb == null) continue;
                    rb.angularVelocity += deltaOmega;
                    rb.velocity += Vector3.Cross(deltaOmega, rb.worldCenterOfMass - com);
                }
                _remaining -= fixedDeltaTime;
                return _remaining > 0f;
            }

            public void Abort() { }   // stopping the forcing is the whole cleanup
        }
    }
}
