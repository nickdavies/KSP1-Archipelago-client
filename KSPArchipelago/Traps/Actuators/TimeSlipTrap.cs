using UnityEngine;

namespace KSPArchipelago.Traps.Actuators
{
    /// <summary>
    /// Warp gremlin. Already warping: shove the current rate index up or down
    /// 1-2 steps. Not warping: kick off a fresh random warp — physics warp
    /// when in atmosphere or landed (on-rails warp is illegal there), on-rails
    /// otherwise (KSP's own altitude limiter clamps illegal rates). The only
    /// trap that fires INTO an active warp instead of dropping it, and the
    /// only one that fires vessel-free from the Space Center / Tracking
    /// Station scenes (null vessel → plain on-rails warp).
    /// </summary>
    internal sealed class TimeSlipTrap : ITrapActuator
    {
        public string ItemName => "Trap: Time Slip";
        public bool ManagesWarpItself => true;
        public bool CanFireWithoutVessel => true;

        public bool IsEligible(Vessel v) => TimeWarp.fetch != null;

        public void Fire(MonoBehaviour host, Vessel v, TrapEffectsRunner runner)
        {
            TimeWarp warp = TimeWarp.fetch;
            int currentIndex = TimeWarp.CurrentRateIndex;

            if (currentIndex > 0)
            {
                int maxIndex = (warp.Mode == TimeWarp.Modes.LOW
                    ? warp.physicsWarpRates.Length
                    : warp.warpRates.Length) - 1;
                int step = (Random.value < 0.5f ? -1 : 1) * Random.Range(1, 3);
                int target = Mathf.Clamp(currentIndex + step, 0, maxIndex);
                if (target != currentIndex)
                    TimeWarp.SetRate(target, false);
            }
            else if (v != null && (v.LandedOrSplashed || v.atmDensity > 1e-4))
            {
                warp.Mode = TimeWarp.Modes.LOW;
                TimeWarp.SetRate(Random.Range(1, warp.physicsWarpRates.Length), false);
            }
            else
            {
                warp.Mode = TimeWarp.Modes.HIGH;
                int maxIndex = warp.warpRates.Length - 1;
                TimeWarp.SetRate(Mathf.Clamp(Random.Range(1, 6), 1, maxIndex), false);
            }

            ScreenMessages.PostScreenMessage(
                "<color=orange>TRAP:</color> Time slips through your fingers...",
                5f, ScreenMessageStyle.UPPER_CENTER);
        }
    }
}
