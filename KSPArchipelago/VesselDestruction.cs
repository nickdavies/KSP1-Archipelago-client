using System;
using System.Collections;
using System.Collections.Generic;
using KSP.UI.Screens;
using UnityEngine;

namespace KSPArchipelago
{
    /// <summary>
    /// Destroys a vessel with a "rapid unplanned disassembly", shared by the
    /// launch-pad over-mass gate and the undiscovered-body collision gate.
    ///
    /// A loaded, unpacked vessel is blown apart part-by-part (Part.explode); an
    /// on-rails / packed vessel is removed with Vessel.Die(). The kill always
    /// defers at least a frame so vessel.parts isn't mutated inside the GameEvent
    /// that triggered it; pass a larger <paramref name="delay"/> (the pad uses
    /// one) for a visible countdown before a scene-entry RUD.
    ///
    /// Every call is honoured — there is deliberately no per-vessel "already
    /// destroyed" memory here. Revert-to-Launch reloads the craft with the SAME
    /// persistentId, so any such memory would silently let a reverted over-mass
    /// craft launch past the pad gate a second time. Each trigger owns its own
    /// once-ness (the pad gate is unconditional, the received-DeathLink path
    /// drains its pending death before calling); the kill itself is made
    /// idempotent by a live check of vessel.state, which cannot go stale.
    /// </summary>
    public static class VesselDestruction
    {
        /// <summary>
        /// Raised on every kill request, before the kill is scheduled.
        /// MissionTracker subscribes to settle that vessel's DeathLink bookkeeping:
        /// the resulting explosion must never rebroadcast a death, and reverting
        /// away from a wreck the mod created must never be billed as a save-scum.
        /// </summary>
        public static event Action<Vessel> Destroyed;

        /// <param name="messageTitle">
        /// When non-empty, also adds a persistent Message-System tray entry (the
        /// pad wants a record). The SOI-collision path leaves it null → the
        /// on-screen toast only.
        /// </param>
        public static void Destroy(
            MonoBehaviour host, Vessel vessel, string screenMessage,
            string messageTitle = null, string messageBody = null, float delay = 0f)
        {
            if (host == null || vessel == null) return;

            Destroyed?.Invoke(vessel);

            if (!string.IsNullOrEmpty(screenMessage))
                ScreenMessages.PostScreenMessage(screenMessage, 8f, ScreenMessageStyle.UPPER_CENTER);

            if (!string.IsNullOrEmpty(messageTitle) && MessageSystem.Instance != null)
                MessageSystem.Instance.AddMessage(new MessageSystem.Message(
                    messageTitle, messageBody,
                    MessageSystemButton.MessageButtonColor.ORANGE,
                    MessageSystemButton.ButtonIcons.FAIL));

            host.StartCoroutine(DestroyAfter(vessel, delay));
        }

        private static IEnumerator DestroyAfter(Vessel vessel, float delay)
        {
            // Drop out of time-warp first. An on-rails, packed vessel under warp
            // won't actually die/explode until it unpacks, so a warping transfer
            // would sail through the "destroyed" body untouched.
            if (TimeWarp.CurrentRate > 1f)
                TimeWarp.SetRate(0, true);

            // Always defer at least one frame — never mutate vessel.parts inside
            // the GameEvent handler that requested the destruction.
            if (delay > 0f) yield return new WaitForSeconds(delay);
            else yield return null;

            // Already dead (a second trigger landed inside this one's countdown):
            // nothing left to explode, and Vessel.Die() is not idempotent.
            if (vessel == null || vessel.state == Vessel.State.DEAD) yield break;

            Debug.Log($"[KSP-AP] Destroying vessel '{vessel.vesselName}' " +
                      $"(loaded={vessel.loaded}, packed={vessel.packed})");

            if (vessel.loaded && !vessel.packed && vessel.parts != null && vessel.parts.Count > 0)
            {
                // Copy first — Part.explode() mutates vessel.parts.
                var parts = new List<Part>(vessel.parts);
                foreach (var p in parts)
                    if (p != null) p.explode();
            }
            else
            {
                // On-rails / packed: no physics parts to explode.
                vessel.Die();
            }
        }
    }
}
