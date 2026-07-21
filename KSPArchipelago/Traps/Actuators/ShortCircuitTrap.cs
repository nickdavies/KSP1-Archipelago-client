using UnityEngine;

namespace KSPArchipelago.Traps.Actuators
{
    /// <summary>
    /// Timed electric-charge drain: bleeds 5x the charge the vessel held at
    /// fire time over 5-15 seconds — generation (solar, RTG, alternators)
    /// can't outrun it, so the banks pin at zero for the window and recover
    /// once the short clears. The drained charge itself is gone (nothing to
    /// restore on abort).
    /// </summary>
    internal sealed class ShortCircuitTrap : ITrapActuator
    {
        public string ItemName => "Trap: Short Circuit";
        public bool ManagesWarpItself => false;
        public bool CanFireWithoutVessel => false;

        // Drain 5x the held charge over the window, not 1x: generation races
        // the drain, and at 1x even a single engine alternator (~6 EC/s)
        // keeps a small battery from ever emptying. 5x outpaces any
        // early-game generation so the brown-out actually happens.
        private const double DrainMultiplier = 5.0;

        public bool IsEligible(Vessel v)
        {
            if (v.packed) return false;
            v.GetConnectedResourceTotals(PartResourceLibrary.ElectricityHashcode,
                out double amount, out double _);
            return amount > 5.0;
        }

        public void Fire(MonoBehaviour host, Vessel v, TrapEffectsRunner runner)
        {
            v.GetConnectedResourceTotals(PartResourceLibrary.ElectricityHashcode,
                out double amount, out double _);
            float duration = Random.Range(5f, 15f);
            runner.Add(new DrainEffect(v, DrainMultiplier * amount / duration, duration));
            ScreenMessages.PostScreenMessage(
                "<color=orange>TRAP:</color> Sparks fly — electric charge is draining fast!",
                5f, ScreenMessageStyle.UPPER_CENTER);
        }

        private sealed class DrainEffect : ITrapEffect
        {
            private readonly Vessel _vessel;
            private readonly double _ratePerSecond;
            private float _remaining;

            public DrainEffect(Vessel vessel, double ratePerSecond, float duration)
            {
                _vessel = vessel;
                _ratePerSecond = ratePerSecond;
                _remaining = duration;
            }

            public bool Tick(float fixedDeltaTime)
            {
                if (_vessel == null || !_vessel.loaded || _vessel.packed
                    || FlightGlobals.ActiveVessel != _vessel)
                    return false;
                _vessel.rootPart.RequestResource(
                    PartResourceLibrary.ElectricityHashcode,
                    _ratePerSecond * fixedDeltaTime,
                    ResourceFlowMode.ALL_VESSEL);
                _remaining -= fixedDeltaTime;
                return _remaining > 0f;
            }

            public void Abort() { }   // drained charge stays drained
        }
    }
}
