using System.Collections.Generic;
using UnityEngine;

namespace KSPArchipelago.Traps.Actuators
{
    /// <summary>
    /// One peripheral leaf part rattles loose and falls off — permanently.
    /// Never the root, a command module, a crewed part, a parachute, a heat
    /// shield (ablator), or the vessel's only engine; weighted toward true
    /// peripherals (instruments, lights, antennas, panels, batteries) so
    /// the loss is a workaround, not a mission kill.
    /// </summary>
    internal sealed class LooseBoltsTrap : ITrapActuator
    {
        public string ItemName => "Trap: Loose Bolts";
        public bool ManagesWarpItself => false;
        public bool CanFireWithoutVessel => false;

        private const int PeripheralWeight = 4;

        public bool IsEligible(Vessel v)
            => !v.packed && WeightedCandidates(v).Count > 0;

        public void Fire(MonoBehaviour host, Vessel v, TrapEffectsRunner runner)
        {
            List<Part> pool = WeightedCandidates(v);
            Part victim = pool[Random.Range(0, pool.Count)];
            string title = victim.partInfo.title;

            // Capture the eject direction BEFORE decoupling — parent and
            // attach-node links go null afterwards.
            Vector3 ejectDir = EjectDirection(victim);
            victim.decouple(0f);
            runner.Add(new NudgeEffect(victim, ejectDir));

            ScreenMessages.PostScreenMessage(
                $"<color=orange>TRAP:</color> {title} rattled loose and fell off!",
                6f, ScreenMessageStyle.UPPER_CENTER);
        }

        /// <summary>Candidate list with peripherals entered multiple times —
        /// a uniform pick over it IS the weighted pick.</summary>
        private static List<Part> WeightedCandidates(Vessel v)
        {
            int engines = 0;
            foreach (Part p in v.parts)
                if (p.FindModuleImplementing<ModuleEngines>() != null) engines++;

            var pool = new List<Part>();
            foreach (Part p in v.parts)
            {
                if (p.children.Count > 0 || p.parent == null) continue;   // leaves only
                if (p.FindModuleImplementing<ModuleCommand>() != null) continue;
                if (p.protoModuleCrew.Count > 0 || p.CrewCapacity > 0) continue;
                if (p.FindModuleImplementing<ModuleParachute>() != null) continue;
                if (p.FindModuleImplementing<ModuleAblator>() != null) continue;
                if (engines == 1
                    && p.FindModuleImplementing<ModuleEngines>() != null) continue;

                int weight = IsPeripheral(p) ? PeripheralWeight : 1;
                for (int i = 0; i < weight; i++) pool.Add(p);
            }
            return pool;
        }

        /// <summary>
        /// World-space direction the shed part should drift: straight out of
        /// its attachment. Surface-attached parts (radial panels,
        /// instruments) push off the mount surface; stack-attached parts
        /// push along the stack axis away from the parent; anything
        /// unrecognized falls back to radially away from the parent part.
        /// </summary>
        private static Vector3 EjectDirection(Part p)
        {
            if (p.srfAttachNode != null && p.srfAttachNode.attachedPart == p.parent)
                return -p.partTransform.TransformDirection(
                    p.srfAttachNode.orientation).normalized;
            foreach (AttachNode node in p.attachNodes)
                if (node.attachedPart == p.parent)
                    return -p.partTransform.TransformDirection(
                        node.orientation).normalized;
            return (p.partTransform.position - p.parent.partTransform.position).normalized;
        }

        /// <summary>
        /// Imparts the drift kick once the shed part has a rigidbody.
        /// Physicsless parts (most panels and instruments) get one only
        /// after KSP promotes the decoupled part to its own vessel — a
        /// same-frame kick silently no-ops (play-tested: zero drift). The
        /// kick stays gentle: slow enough that an engineer with a jetpack
        /// can chase the part down and stick it back on.
        /// </summary>
        private sealed class NudgeEffect : ITrapEffect
        {
            private readonly Part _part;
            private readonly Vector3 _direction;
            private float _remaining = 1f;   // give-up window for the rb

            public NudgeEffect(Part part, Vector3 direction)
            {
                _part = part;
                _direction = direction;
            }

            public bool Tick(float fixedDeltaTime)
            {
                if (_part == null || _part.State == PartStates.DEAD) return false;
                if (_part.rb != null)
                {
                    _part.rb.AddForce(
                        _direction * Random.Range(0.1f, 0.3f),
                        ForceMode.VelocityChange);
                    // A slow tumble sells "rattled loose" better than a
                    // clean translation.
                    _part.rb.angularVelocity +=
                        Random.onUnitSphere * Random.Range(0.2f, 0.5f);
                    return false;
                }
                _remaining -= fixedDeltaTime;
                return _remaining > 0f;
            }

            public void Abort() { }
        }

        private static bool IsPeripheral(Part p)
            => p.FindModuleImplementing<ModuleScienceExperiment>() != null
               || p.FindModuleImplementing<ModuleLight>() != null
               || p.FindModuleImplementing<ModuleDataTransmitter>() != null
               || p.FindModuleImplementing<ModuleDeployableSolarPanel>() != null
               || p.Resources.Contains("ElectricCharge");

        // ModuleEngines lookup helper type note: ModuleEnginesFX derives from
        // ModuleEngines, so FindModuleImplementing<ModuleEngines> covers both.
    }
}
