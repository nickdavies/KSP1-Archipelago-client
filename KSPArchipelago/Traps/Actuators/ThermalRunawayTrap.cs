using System.Collections.Generic;
using UnityEngine;

namespace KSPArchipelago.Traps.Actuators
{
    /// <summary>
    /// Heats one random non-critical part toward 85% of its max temperature
    /// over roughly 5-10 seconds via AddThermalFlux (kJ/s against
    /// thermalMass in kJ/K, so flux = thermalMass * deltaT / time, then
    /// overdriven — see Overdrive), stopping at the 85% ceiling or a 30s
    /// timeout. Drama on the stock thermal gauges, not a guaranteed
    /// explosion — flying aggressively past the ceiling is on the player.
    /// Never targets the root part, a command module, or a crewed part.
    /// </summary>
    internal sealed class ThermalRunawayTrap : ITrapActuator
    {
        public string ItemName => "Trap: Thermal Runaway";
        public bool ManagesWarpItself => false;
        public bool CanFireWithoutVessel => false;

        private const double TargetFraction = 0.85;

        public bool IsEligible(Vessel v)
            => !v.packed && Candidates(v).Count > 0;

        public void Fire(MonoBehaviour host, Vessel v, TrapEffectsRunner runner)
        {
            List<Part> candidates = Candidates(v);
            Part victim = candidates[Random.Range(0, candidates.Count)];
            runner.Add(new HeatEffect(v, victim, Random.Range(5f, 10f)));
            ScreenMessages.PostScreenMessage(
                $"<color=orange>TRAP:</color> {victim.partInfo.title} is heating up fast!",
                5f, ScreenMessageStyle.UPPER_CENTER);
        }

        private static List<Part> Candidates(Vessel v)
        {
            var result = new List<Part>();
            foreach (Part p in v.parts)
            {
                if (p == v.rootPart) continue;
                if (p.FindModuleImplementing<ModuleCommand>() != null) continue;
                if (p.protoModuleCrew.Count > 0) continue;
                if (p.temperature >= 0.7 * p.maxTemp) continue;   // already hot — no headroom
                result.Add(p);
            }
            return result;
        }

        private sealed class HeatEffect : ITrapEffect
        {
            private const float Timeout = 30f;

            // A gap-proportional flux decays as the part heats and loses the
            // race against KSP's thermal sim (internal->skin conduction plus
            // radiative/convective losses): play-tested stalls at ~65% of
            // maxTemp. So drive at a CONSTANT rate sized from the initial
            // gap, overdriven 4x — it punches through the losses and the
            // per-tick 85% ceiling check stops it (one tick adds ~1-2% of
            // the initial gap, so no overshoot explosion).
            private const double Overdrive = 4.0;

            private readonly Vessel _vessel;
            private readonly Part _part;
            private readonly double _flux;
            private float _elapsed;

            public HeatEffect(Vessel vessel, Part part, float window)
            {
                _vessel = vessel;
                _part = part;
                _flux = Overdrive * part.thermalMass
                    * (TargetFraction * part.maxTemp - part.temperature) / window;
            }

            public bool Tick(float fixedDeltaTime)
            {
                if (_vessel == null || !_vessel.loaded || _vessel.packed
                    || FlightGlobals.ActiveVessel != _vessel)
                    return false;
                if (_part == null || _part.vessel != _vessel
                    || _part.State == PartStates.DEAD)
                    return false;

                if (_part.temperature >= TargetFraction * _part.maxTemp)
                    return false;
                _elapsed += fixedDeltaTime;
                if (_elapsed > Timeout) return false;

                if (_flux > 0.0) _part.AddThermalFlux(_flux);
                return true;
            }

            public void Abort() { }   // heat dissipates on its own
        }
    }
}
