using System.Collections.Generic;
using UnityEngine;

namespace KSPArchipelago.Traps.Actuators
{
    /// <summary>
    /// 30-90 seconds of total comms blackout: every antenna's antennaPower
    /// is zeroed and restored when the static clears (or on abort).
    /// antennaPower is config-loaded and never persisted, so a crash
    /// mid-trap self-heals on reload. Stock-authentic consequence: an
    /// uncrewed probe with no link loses control input — that's CommNet.
    /// In a save with CommNet disabled the trap fires as a harmless crackle
    /// (consumed — it must never clog the queue waiting for a setting).
    ///
    /// Overlapping copies COALESCE into one longer blackout. They must not
    /// nest: a second effect would capture the already-zeroed antennaPower as
    /// its "original", and whichever finished last would restore 0.0 —
    /// permanently dead antennas.
    /// </summary>
    internal sealed class RadioSilenceTrap : ITrapActuator
    {
        public string ItemName => "Trap: Radio Silence";
        public bool ManagesWarpItself => false;
        public bool CanFireWithoutVessel => false;

        public bool IsEligible(Vessel v)
            => !v.packed
               && v.FindPartModulesImplementing<ModuleDataTransmitter>().Count > 0;

        public void Fire(MonoBehaviour host, Vessel v, TrapEffectsRunner runner)
        {
            if (HighLogic.CurrentGame == null
                || !HighLogic.CurrentGame.Parameters.Difficulty.EnableCommNet)
            {
                ScreenMessages.PostScreenMessage(
                    "<color=orange>TRAP:</color> Your antennas crackle... nothing happens.",
                    5f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }

            float duration = Random.Range(30f, 90f);
            SilenceEffect live = SilenceEffect.Live;
            if (live != null && live.Covers(v))
            {
                live.Extend(duration);
                Debug.Log($"[KSP-AP] Radio Silence coalesced into the live blackout ({duration:F0}s)");
            }
            else
            {
                runner.Add(new SilenceEffect(v, duration));
            }

            ScreenMessages.PostScreenMessage(
                "<color=orange>TRAP:</color> Static floods every frequency!",
                5f, ScreenMessageStyle.UPPER_CENTER);
        }

        private sealed class SilenceEffect : ITrapEffect
        {
            /// <summary>The one live blackout, or null. Set in the ctor, cleared
            /// by Restore — every path out of the effect goes through Restore.</summary>
            internal static SilenceEffect Live;

            private readonly Vessel _vessel;
            private readonly List<KeyValuePair<ModuleDataTransmitter, double>> _saved;
            private float _remaining;

            /// <summary>Only coalesce onto the vessel this blackout actually
            /// holds — a different craft has a disjoint antenna set, so a second
            /// effect there is safe.</summary>
            internal bool Covers(Vessel v) => _vessel == v;

            /// <summary>Absorb an overlapping copy: the blackout lasts as long as
            /// the longer of the two, and the antennas are only saved once.</summary>
            internal void Extend(float duration)
                => _remaining = Mathf.Max(_remaining, duration);

            public SilenceEffect(Vessel vessel, float duration)
            {
                Live = this;
                _vessel = vessel;
                _remaining = duration;
                _saved = new List<KeyValuePair<ModuleDataTransmitter, double>>();
                foreach (ModuleDataTransmitter antenna
                         in vessel.FindPartModulesImplementing<ModuleDataTransmitter>())
                {
                    _saved.Add(new KeyValuePair<ModuleDataTransmitter, double>(
                        antenna, antenna.antennaPower));
                    antenna.antennaPower = 0.0;
                }
            }

            public bool Tick(float fixedDeltaTime)
            {
                if (_vessel == null || !_vessel.loaded
                    || FlightGlobals.ActiveVessel != _vessel)
                {
                    Restore();
                    return false;
                }
                _remaining -= fixedDeltaTime;
                if (_remaining > 0f) return true;
                Restore();
                ScreenMessages.PostScreenMessage(
                    "The static clears — comms restored.",
                    4f, ScreenMessageStyle.UPPER_CENTER);
                return false;
            }

            public void Abort() => Restore();

            private void Restore()
            {
                foreach (var entry in _saved)
                    if (entry.Key != null)
                        entry.Key.antennaPower = entry.Value;
                _saved.Clear();
                if (Live == this) Live = null;
            }
        }
    }
}
