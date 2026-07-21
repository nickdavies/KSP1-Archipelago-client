using KSP.UI.Screens;
using UnityEngine;

namespace KSPArchipelago.Traps.Actuators
{
    /// <summary>
    /// A 15-second fuse, then the next stage fires — no cancel button. One
    /// warning at ten seconds, a hard red count from five. The one defense:
    /// if the manual stage lock (Alt+L) is engaged when the timer hits
    /// zero, the lock wins and the trap is spent — hammering the lock key
    /// in time IS the minigame.
    /// </summary>
    internal sealed class StageFrightTrap : ITrapActuator
    {
        public string ItemName => "Trap: Stage Fright";
        public bool ManagesWarpItself => false;
        public bool CanFireWithoutVessel => false;

        public bool IsEligible(Vessel v)
            => !v.packed && v.IsControllable && StageManager.CurrentStage > 0;

        public void Fire(MonoBehaviour host, Vessel v, TrapEffectsRunner runner)
        {
            runner.Add(new CountdownEffect(v));
        }

        private sealed class CountdownEffect : ITrapEffect
        {
            private readonly Vessel _vessel;
            private float _remaining = 15f;
            private int _lastPosted = int.MaxValue;
            // One live message replaced each second — five stacked posts
            // didn't read as a countdown in play-test.
            private ScreenMessage _message;

            public CountdownEffect(Vessel vessel)
            {
                _vessel = vessel;
            }

            public bool Tick(float fixedDeltaTime)
            {
                if (_vessel == null || !_vessel.loaded || _vessel.packed
                    || FlightGlobals.ActiveVessel != _vessel)
                {
                    ClearMessage();
                    return false;   // vessel changed mid-countdown — silent fizzle
                }

                // One warning at ten, then the hard count from five.
                int whole = Mathf.CeilToInt(_remaining);
                if (whole < _lastPosted && (whole == 10 || (whole <= 5 && whole >= 1)))
                {
                    _lastPosted = whole;
                    ClearMessage();
                    _message = ScreenMessages.PostScreenMessage(
                        $"<color=red>TRAP: STAGING IN {whole}...</color>",
                        whole == 10 ? 3f : 1.5f, ScreenMessageStyle.UPPER_CENTER);
                }

                _remaining -= fixedDeltaTime;
                if (_remaining > 0f) return true;
                ClearMessage();

                if (FlightInputHandler.fetch != null && FlightInputHandler.fetch.stageLock)
                    ScreenMessages.PostScreenMessage(
                        "<color=orange>TRAP:</color> The stage lock holds... this time.",
                        5f, ScreenMessageStyle.UPPER_CENTER);
                else if (StageManager.CurrentStage > 0)
                    StageManager.ActivateNextStage();
                return false;
            }

            // A scene change mid-countdown kills the timer; the trap was
            // already consumed at Fire — reverting to dodge it is the same
            // escape hatch every trap has.
            public void Abort() => ClearMessage();

            private void ClearMessage()
            {
                if (_message == null) return;
                ScreenMessages.RemoveMessage(_message);
                _message = null;
            }
        }
    }
}
