using UnityEngine;

namespace KSPArchipelago.Traps
{
    /// <summary>
    /// One trap type's in-game effect. Stateless; registered once in
    /// TrapManager, keyed by the exact AP item name (no string parsing —
    /// the item name is the entire wire signal).
    /// </summary>
    public interface ITrapActuator
    {
        /// <summary>Exact AP item name — the registry key.</summary>
        string ItemName { get; }

        /// <summary>
        /// True only for traps whose effect IS warp manipulation (Time Slip).
        /// Every other trap gets time-warp dropped before Fire.
        /// </summary>
        bool ManagesWarpItself { get; }

        /// <summary>
        /// True for traps that need no active vessel and can fire from the
        /// Space Center / Tracking Station scenes (Time Slip — both scenes
        /// support warp). IsEligible and Fire receive a null vessel there
        /// and must tolerate it.
        /// </summary>
        bool CanFireWithoutVessel { get; }

        /// <summary>
        /// Pure predicate: can this trap meaningfully fire on this vessel
        /// right now? An ineligible trap stays queued for a later vessel.
        /// Null vessel only occurs when CanFireWithoutVessel is true.
        /// </summary>
        bool IsEligible(Vessel v);

        /// <summary>
        /// Actuate on the main thread on the active vessel (null only when
        /// CanFireWithoutVessel, from a non-flight scene). One-shot traps act
        /// directly; timed traps register an ITrapEffect on the runner.
        /// Throwing marks the trap fizzled-but-consumed.
        /// </summary>
        void Fire(MonoBehaviour host, Vessel v, TrapEffectsRunner runner);
    }

    /// <summary>
    /// A received, not-yet-fired trap. The received-item index is the
    /// exactly-once identity — the same index space as
    /// ApScenarioModule.AwardedItemIndices and FiredTrapStore.
    /// </summary>
    public struct PendingTrap
    {
        public int Index;
        public string Name;
    }
}
