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
    /// one) for a visible countdown before a scene-entry RUD. Repeated calls for
    /// the same vessel are ignored, so a trigger that fires multiple times can't
    /// stack notifications or explosions.
    /// </summary>
    public static class VesselDestruction
    {
        private static readonly HashSet<uint> _destroying = new HashSet<uint>();

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
            if (!_destroying.Add(vessel.persistentId)) return;   // already being destroyed

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

            if (vessel == null) yield break;

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
