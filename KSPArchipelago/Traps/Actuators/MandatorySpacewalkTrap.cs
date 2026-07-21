using System.Collections.Generic;
using UnityEngine;

namespace KSPArchipelago.Traps.Actuators
{
    /// <summary>
    /// A random crew member suddenly needs some fresh vacuum: they are
    /// spawned out an airlock in ANY situation — atmo flight, sub-orbital,
    /// whatever — and focus follows them. Climbing back in (or the dramatic
    /// mid-air catch) is the player's problem. If KSP refuses the spawn
    /// (obstructed hatch, career gate), the throw is caught upstream and
    /// the trap fizzles-but-consumes.
    /// </summary>
    internal sealed class MandatorySpacewalkTrap : ITrapActuator
    {
        public string ItemName => "Trap: Mandatory Spacewalk";
        public bool ManagesWarpItself => false;
        public bool CanFireWithoutVessel => false;

        public bool IsEligible(Vessel v)
        {
            if (v.packed || FlightEVA.fetch == null) return false;
            foreach (Part p in v.parts)
                if (p.protoModuleCrew.Count > 0 && p.airlock != null)
                    return true;
            return false;
        }

        public void Fire(MonoBehaviour host, Vessel v, TrapEffectsRunner runner)
        {
            var doors = new List<Part>();
            foreach (Part p in v.parts)
                if (p.protoModuleCrew.Count > 0 && p.airlock != null)
                    doors.Add(p);

            Part door = doors[Random.Range(0, doors.Count)];
            ProtoCrewMember victim =
                door.protoModuleCrew[Random.Range(0, door.protoModuleCrew.Count)];
            string name = victim.name;

            ScreenMessages.PostScreenMessage(
                $"<color=orange>TRAP:</color> {name} suddenly needs some fresh... vacuum.",
                6f, ScreenMessageStyle.UPPER_CENTER);
            FlightEVA.fetch.spawnEVA(victim, door, door.airlock, true);
        }
    }
}
