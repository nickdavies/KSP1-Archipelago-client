using System;
using System.Collections.Generic;
using KSP.UI.Screens.Mapview;
using UnityEngine;

namespace KSPArchipelago
{
    /// <summary>
    /// Hides and reveals celestial bodies at runtime, reversibly. Two layers,
    /// mirroring the ResearchBodies mechanism:
    ///
    ///  * "undiscovered" (applied to every hidden body, shallow OR deep): the
    ///    DiscoveryInfo level is lowered to Presence, the map-node icon is
    ///    suppressed, and the body is removed from the tracking-station Tab-target
    ///    cycle. The scaled-space sphere still renders — the body is visible but
    ///    unlabelled and unselectable.
    ///
    ///  * "deep" (applied only to deep-hidden bodies): additionally the scaled-space
    ///    renderers are disabled, so the body is fully invisible.
    ///
    /// The hidden set (bodyName -> deep?) is static so it survives scene changes.
    /// KSP rebuilds map nodes and the Tab-target list per scene, and the scaled
    /// space / PQS toggles are persistent flags on DontDestroyOnLoad objects, so
    /// <see cref="ReapplyAll"/> re-asserts the whole set on each scene load and
    /// <see cref="RefreshMapLayer"/> re-asserts the map-only layer on each map
    /// entry. Reveal is always immediate.
    ///
    /// This is deliberately usable WITHOUT an AP connection — the debug UI drives
    /// <see cref="HideBody"/> / <see cref="RevealBody"/> directly; the slot-data
    /// wiring layers on top later.
    /// </summary>
    public static class BodyUnlockManager
    {
        // bodyName -> deep?  Absent from the dict = visible.
        private static readonly Dictionary<string, bool> _hidden = new Dictionary<string, bool>();

        // Discover-item name -> bodyName, from slot_data (body_item_map).
        private static readonly Dictionary<string, string> _itemToBody = new Dictionary<string, string>();

        // Bodies revealed by flying to them (not via an item). Kept so a scene
        // reload's ApplyConfiguredHidden doesn't re-hide them. In-session only —
        // cross-session recovery of fly-there reveals is a later (AP DataStorage)
        // refinement; item reveals already replay from AP received-items.
        private static readonly HashSet<string> _flownReveals = new HashSet<string>();

        // Scaled-space components we switched off for a deep-hidden body, keyed by
        // bodyName. Only these are switched back on at reveal, so anything a
        // visual mod keeps disabled on purpose (e.g. Scatterer's replaced
        // atmosphere shell) is left alone.
        private sealed class ScaledHide
        {
            public readonly HashSet<Renderer> Renderers = new HashSet<Renderer>();
            public readonly HashSet<Behaviour> Drivers = new HashSet<Behaviour>();
        }
        private static readonly Dictionary<string, ScaledHide> _scaledHidden = new Dictionary<string, ScaledHide>();

        // When true, undiscovered bodies may be flown to (arriving reveals them)
        // and hiding is shallow; when false, entering one destroys the craft and
        // hiding is deep (fully invisible).
        private static bool _allowUndiscovered = true;

        /// <summary>
        /// When true, entering a hidden body's SOI reveals it (fly-to-reveal);
        /// when false, it destroys the craft. Settable so the debug UI can flip
        /// the live SOI behaviour without re-hiding; Configure() also sets it from
        /// slot_data.
        /// </summary>
        public static bool AllowUndiscovered
        {
            get => _allowUndiscovered;
            set => _allowUndiscovered = value;
        }

        // Hide depth for the configured set, INDEPENDENT of the allow/block
        // choice: true = deep (invisible, the default), false = shallow (visible
        // but unlabelled). Set from slot_data (deep_hide, default true); the debug
        // UI also toggles it.
        private static bool _deepHide = true;
        public static bool DeepHide
        {
            get => _deepHide;
            set => _deepHide = value;
        }

        public static bool IsHidden(string bodyName) => _hidden.ContainsKey(bodyName);

        public static bool IsDeep(string bodyName) =>
            _hidden.TryGetValue(bodyName, out bool deep) && deep;

        public static IEnumerable<string> HiddenBodies => _hidden.Keys;

        // ------------------------------------------------------------------
        // Public API (also what the debug UI calls)
        // ------------------------------------------------------------------

        public static void HideBody(CelestialBody body, bool deep)
        {
            if (body == null) return;
            _hidden[body.bodyName] = deep;
            Apply(body, hidden: true, deep: deep);
            Debug.Log($"[KSP-AP] Body hidden ({(deep ? "deep" : "shallow")}): {body.bodyName}");
        }

        public static void RevealBody(CelestialBody body)
        {
            if (body == null) return;
            _hidden.Remove(body.bodyName);
            Apply(body, hidden: false, deep: false);
            Debug.Log($"[KSP-AP] Body revealed: {body.bodyName}");
        }

        public static void RevealAll()
        {
            // Copy the names first — Apply is a no-op on the dict but keep this
            // robust against future changes.
            var names = new List<string>(_hidden.Keys);
            _hidden.Clear();
            foreach (var name in names)
            {
                var body = FlightGlobals.GetBodyByName(name);
                if (body != null) Apply(body, hidden: false, deep: false);
            }
        }

        // ------------------------------------------------------------------
        // Slot-data-driven configuration (layered over the primitives above)
        // ------------------------------------------------------------------

        /// <summary>
        /// Record the seed's Discover-item → body map and the allow/destruction
        /// flag. Excludes the home body and any star as a safety backstop (the
        /// server should already exclude them). Does not hide anything — call
        /// <see cref="ApplyConfiguredHidden"/>.
        /// </summary>
        public static void Configure(IDictionary<string, string> itemToBody, bool allowUndiscovered)
        {
            _itemToBody.Clear();
            _allowUndiscovered = allowUndiscovered;
            if (itemToBody == null) return;
            foreach (var kv in itemToBody)
            {
                string bodyName = kv.Value;
                var body = FlightGlobals.GetBodyByName(bodyName);
                if (body == null)
                {
                    Debug.LogWarning($"[KSP-AP] hidden-body '{bodyName}' not found in FlightGlobals; skipping");
                    continue;
                }
                if (bodyName == KSPArchipelagoMod.StartingBody || body.isHomeWorld)
                {
                    Debug.LogWarning($"[KSP-AP] refusing to hide home body '{bodyName}'");
                    continue;
                }
                if (body.isStar || body.referenceBody == null)
                {
                    Debug.LogWarning($"[KSP-AP] refusing to hide star '{bodyName}'");
                    continue;
                }
                _itemToBody[kv.Key] = bodyName;
            }
        }

        public static bool TryGetBodyForItem(string itemName, out string bodyName)
            => _itemToBody.TryGetValue(itemName, out bodyName);

        /// <summary>
        /// Hide every configured body at the current <see cref="DeepHide"/> depth.
        /// Skips bodies already revealed by flying there, and bodies that currently
        /// have a mission craft in their SOI (never strand a craft under a hidden
        /// body). Item-based reveals re-open the rest via ProcessAllItems.
        /// </summary>
        public static void ApplyConfiguredHidden()
        {
            bool deep = _deepHide;
            foreach (var bodyName in _itemToBody.Values)
            {
                if (_flownReveals.Contains(bodyName)) continue;
                var body = FlightGlobals.GetBodyByName(bodyName);
                if (body == null) continue;
                if (HasCraftInSoi(body))
                {
                    Debug.Log($"[KSP-AP] not hiding {bodyName} — a mission craft is in its SOI");
                    continue;
                }
                HideBody(body, deep);
            }
        }

        /// <summary>Reveal a body by name (item receipt or fly-there arrival).</summary>
        public static void RevealByName(string bodyName)
        {
            var body = FlightGlobals.GetBodyByName(bodyName);
            if (body != null) RevealBody(body);
        }

        /// <summary>
        /// Mark a body as revealed by flying to it, so a later scene reload's
        /// <see cref="ApplyConfiguredHidden"/> leaves it visible.
        /// </summary>
        public static void MarkFlownReveal(string bodyName)
        {
            if (!string.IsNullOrEmpty(bodyName)) _flownReveals.Add(bodyName);
        }

        /// <summary>The current fly-there reveal set (for persisting to DataStorage).</summary>
        public static IEnumerable<string> FlownReveals => _flownReveals;

        /// <summary>
        /// Seed the fly-there reveal set from persisted DataStorage on connect, so
        /// <see cref="ApplyConfiguredHidden"/> leaves those bodies visible. Also
        /// reveals any that are already hidden (in case the seed arrives after the
        /// initial hide pass).
        /// </summary>
        public static void SeedFlownReveals(IEnumerable<string> names)
        {
            if (names == null) return;
            foreach (var n in names)
            {
                if (string.IsNullOrEmpty(n)) continue;
                _flownReveals.Add(n);
                if (_hidden.ContainsKey(n)) RevealByName(n);
            }
        }

        /// <summary>Tear down on disconnect: reveal everything, forget the config.</summary>
        public static void ResetAll()
        {
            RevealAll();
            _itemToBody.Clear();
            _flownReveals.Clear();
            _allowUndiscovered = true;
            _deepHide = true;
        }

        private static bool HasCraftInSoi(CelestialBody body)
        {
            var vessels = FlightGlobals.Vessels;
            if (vessels == null) return false;
            for (int i = 0; i < vessels.Count; i++)
            {
                var v = vessels[i];
                if (v == null) continue;
                if (v.vesselType == VesselType.Debris || v.vesselType == VesselType.Unknown
                    || v.vesselType == VesselType.SpaceObject || v.vesselType == VesselType.Flag)
                    continue;
                if (v.mainBody == body) return true;
            }
            return false;
        }

        /// <summary>
        /// Re-assert the hidden set in the current scene (DiscoveryInfo + scaled
        /// mesh + PQS + map/camera where available). Call on every scene load.
        /// </summary>
        public static void ReapplyAll()
        {
            foreach (var kv in _hidden)
            {
                var body = FlightGlobals.GetBodyByName(kv.Key);
                if (body != null) Apply(body, hidden: true, deep: kv.Value);
            }
            // Re-assert the REVEALED state for fly-there reveals. A scene rebuild
            // won't restore their map-node icon / camera target / discovery on its
            // own (the reveal fired in flight, where there are no map nodes), which
            // left the tracking-station orbit missing after a fly-by reveal.
            foreach (var name in _flownReveals)
            {
                if (_hidden.ContainsKey(name)) continue;
                var body = FlightGlobals.GetBodyByName(name);
                if (body != null) Apply(body, hidden: false, deep: false);
            }
        }

        // ------------------------------------------------------------------
        // Low-level ops (each defensively guarded — a single bad body must not
        // abort a loop that spans the whole solar system)
        // ------------------------------------------------------------------

        private static void Apply(CelestialBody body, bool hidden, bool deep)
        {
            // Discovery: Presence = "detected but unidentified"; Owned = the stock
            // default (all bodies known). Reveal always restores to Owned.
            try { body.DiscoveryInfo?.SetLevel(hidden ? DiscoveryLevels.Presence : DiscoveryLevels.Owned); }
            catch (Exception e) { Debug.LogWarning($"[KSP-AP] DiscoveryInfo set failed for {body.bodyName}: {e.Message}"); }

            SetMapNodeIcon(body, enabled: !hidden);
            SetCameraTarget(body, present: !hidden);

            // Deep layer: hide the scaled-space object (the far-view sphere). We
            // deliberately do NOT touch the PQS surface — it only renders when
            // you're close to a body, which never happens for a hidden one (BLOCK
            // destroys on SOI entry, ALLOW reveals on entry), and DeactivateSphere
            // throws an NRE on bodies whose PQS isn't active in the current scene.
            SetScaledBodyVisible(body, visible: !(hidden && deep));
        }

        // Deep-hide switches off the scaled-space renderers; it must NOT
        // deactivate the GameObject. That object hosts the body's MapObject
        // (ScaledMovement), and PlanetariumCamera parents its pivot — with the
        // camera underneath — to the targeted MapObject's transform. Kerbin is
        // the prefab initialTarget, targeted on every tracking-station load, so
        // an inactive Kerbin took the whole camera rig inactive with it;
        // SpaceTracking.Start's FindObjectOfType<PlanetariumCamera>() then
        // returned null and threw before the Leave button was wired up.
        //
        // ScaledSpaceFader and AtmosphereFromGround rewrite Renderer.enabled
        // every Update, so they are switched off together with the renderers.
        // Re-asserting an already-hidden body only adds anything that has come
        // back on since; reveal restores exactly the recorded set.
        private static void SetScaledBodyVisible(CelestialBody body, bool visible)
        {
            try
            {
                var scaled = body.scaledBody;
                if (scaled == null) return;
                if (visible)
                {
                    if (!_scaledHidden.TryGetValue(body.bodyName, out var record)) return;
                    _scaledHidden.Remove(body.bodyName);
                    foreach (var d in record.Drivers) if (d != null) d.enabled = true;
                    foreach (var r in record.Renderers) if (r != null) r.enabled = true;
                    return;
                }
                if (!_scaledHidden.TryGetValue(body.bodyName, out var hide))
                {
                    hide = new ScaledHide();
                    _scaledHidden[body.bodyName] = hide;
                }
                foreach (var f in scaled.GetComponentsInChildren<ScaledSpaceFader>(true))
                    if (f.enabled) { f.enabled = false; hide.Drivers.Add(f); }
                foreach (var a in scaled.GetComponentsInChildren<AtmosphereFromGround>(true))
                    if (a.enabled) { a.enabled = false; hide.Drivers.Add(a); }
                foreach (var r in scaled.GetComponentsInChildren<Renderer>(true))
                    if (r.enabled) { r.enabled = false; hide.Renderers.Add(r); }
            }
            catch (Exception e) { Debug.LogWarning($"[KSP-AP] scaled body toggle failed for {body.bodyName}: {e.Message}"); }
        }

        private static void SetCameraTarget(CelestialBody body, bool present)
        {
            try
            {
                if (PlanetariumCamera.fetch == null || body.MapObject == null) return;
                var targets = PlanetariumCamera.fetch.targets;
                bool contains = targets.Contains(body.MapObject);
                if (present && !contains) targets.Add(body.MapObject);
                else if (!present && contains) targets.Remove(body.MapObject);
            }
            catch (Exception e) { Debug.LogWarning($"[KSP-AP] camera-target toggle failed for {body.bodyName}: {e.Message}"); }
        }

        private static void SetMapNodeIcon(CelestialBody body, bool enabled)
        {
            try
            {
                var nodes = MapNode.AllMapNodes;
                if (nodes == null) return;
                for (int i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];
                    if (node != null && node.mapObject != null && node.mapObject.celestialBody == body)
                        node.VisualIconData.iconEnabled = enabled;
                }
            }
            catch (Exception e) { Debug.LogWarning($"[KSP-AP] map-node toggle failed for {body.bodyName}: {e.Message}"); }
        }
    }
}
