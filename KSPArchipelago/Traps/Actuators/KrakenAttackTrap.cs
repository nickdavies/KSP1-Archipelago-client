using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPArchipelago.Traps.Actuators
{
    /// <summary>
    /// A minor kraken skitters through the craft toggling every deployable
    /// it can reach, one every half second for comedy: solar panels,
    /// radiators and deployable antennas flip Extend/Retract, and the
    /// landing gear action group fires once if any wheels/legs exist. Full
    /// chaos anywhere including in-atmo — panels shredding at high Q is the
    /// quirk. Skips parachutes (never), non-retractable already-extended
    /// panels (OX-STAT), and anything mid-animation.
    /// </summary>
    internal sealed class KrakenAttackTrap : ITrapActuator
    {
        public string ItemName => "Trap: Minor Kraken Attack";
        public bool ManagesWarpItself => false;
        public bool CanFireWithoutVessel => false;

        public bool IsEligible(Vessel v)
            => !v.packed && BuildToggles(v).Count > 0;

        public void Fire(MonoBehaviour host, Vessel v, TrapEffectsRunner runner)
        {
            List<Action> toggles = BuildToggles(v);
            // Shuffle so the skitter order differs every time.
            for (int i = toggles.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                Action tmp = toggles[i];
                toggles[i] = toggles[j];
                toggles[j] = tmp;
            }
            runner.Add(new ToggleSequenceEffect(v, toggles));
            ScreenMessages.PostScreenMessage(
                "<color=orange>TRAP:</color> A minor kraken is rattling through the craft...",
                5f, ScreenMessageStyle.UPPER_CENTER);
        }

        private static List<Action> BuildToggles(Vessel v)
        {
            var toggles = new List<Action>();
            foreach (ModuleDeployablePart module
                     in v.FindPartModulesImplementing<ModuleDeployablePart>())
            {
                ModuleDeployablePart m = module;
                if (m.deployState == ModuleDeployablePart.DeployState.EXTENDED
                    && m.retractable)
                    toggles.Add(() => m.Retract());
                else if (m.deployState == ModuleDeployablePart.DeployState.RETRACTED)
                    toggles.Add(() => m.Extend());
                // EXTENDING/RETRACTING/BROKEN and OX-STAT-style fixed panels
                // are skipped.
            }
            if (v.FindPartModulesImplementing<ModuleWheels.ModuleWheelDeployment>().Count > 0)
            {
                Vessel vv = v;
                toggles.Add(() => vv.ActionGroups.ToggleGroup(KSPActionGroup.Gear));
            }
            return toggles;
        }

        private sealed class ToggleSequenceEffect : ITrapEffect
        {
            private const float Interval = 0.5f;

            private readonly Vessel _vessel;
            private readonly List<Action> _toggles;
            private float _untilNext;
            private int _next;

            public ToggleSequenceEffect(Vessel vessel, List<Action> toggles)
            {
                _vessel = vessel;
                _toggles = toggles;
            }

            public bool Tick(float fixedDeltaTime)
            {
                if (_vessel == null || !_vessel.loaded || _vessel.packed
                    || FlightGlobals.ActiveVessel != _vessel)
                    return false;
                _untilNext -= fixedDeltaTime;
                if (_untilNext > 0f) return true;
                _untilNext = Interval;
                _toggles[_next++]();   // a throwing toggle drops the effect (runner catches)
                return _next < _toggles.Count;
            }

            public void Abort() { }   // whatever toggled stays toggled — that's the trap
        }
    }
}
