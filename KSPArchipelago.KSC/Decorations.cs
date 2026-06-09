// Crawlerway + AstronautComplex FlagPole decorations.
//
// Ported from AP_KSC_Probe/TerrainProbe.cs PlaceDecorations.  Unlike
// the KSC building meshes (which are registered as KK StaticModels and
// instantiated through StaticDatabase), these are decoration
// GameObjects that live in the live SpaceCenter scene as part of
// Kerbin's KSC.  We clone them and reparent to our alien GroupCenter
// so they appear in the same relative position to the alien
// LaunchPad as their source does relative to Kerbin's LaunchPad.
//
// Two source GameObjects are looked up by name in the live scene:
//   - Crawlerway   (the road from VAB to LaunchPad)
//   - flagPole_lev1 (the AstronautComplex flag mesh; located inside
//                    KSC upgrade prefabs)
//
// Each is measured against Kerbin's stock LaunchPad: visible mesh
// AABB centre or base (anchor base for the flag) is converted to
// (east, north, up) offsets in the LaunchPad's local frame.  The
// alien clone is then placed at the same offsets in the alien
// GroupCenter's local frame, with the mesh's pivot back-computed so
// the visible centre lands where we want (some Squad source meshes
// have their pivot meaningfully offset from the visible centre).

using System;
using System.Collections;
using System.Collections.Generic;
using KerbalKonstructs.Core;
using UnityEngine;

namespace KSPArchipelago.KSC
{
    public struct DecorationSpec
    {
        public string Name;
        public string[] SearchNames;
        public double East, North, Up;
        // AnchorBase = true: place the visual BASE of the mesh on the
        // pad (override the Kerbin-side vUp measurement).
        public bool AnchorBase;
        // UseExplicitPosition = true: ignore the source GameObject's
        // own world position and use the spec's East/North/Up instead.
        public bool UseExplicitPosition;
    }

    public static class Decorations
    {
        public static readonly DecorationSpec[] DECORATIONS = new[]
        {
            new DecorationSpec
            {
                Name = "Crawlerway",
                SearchNames = new[] { "Crawlerway" },
                East = -350.0, North = 2.4, Up = -0.3,
            },
            new DecorationSpec
            {
                Name = "FlagPole_AC",
                // SearchNames unused — FindACFlagSource walks the AC
                // UpgradeableObject's active facilityInstance directly,
                // bypassing the probe's broken name-based search that
                // matched only inactive Administration upgrade prefabs.
                SearchNames = new string[0],
                East = -1006.3, North = 31.8, Up = -5.0,
                AnchorBase = true,
                UseExplicitPosition = true,
            },
        };

        // ~10 s @ 60 fps for the readiness poll to give up.  Hitting this
        // bound means the KSC scene hierarchy has shifted (KSP update?) or
        // the host scene isn't actually SpaceCenter (e.g. ApplyServerBody
        // fired from MainMenu/FLIGHT).  Either way: loud failure, not silent.
        public const int MaxReadinessFrames = 600;

        // Bodies whose decorations have already been placed this KSP session.
        // SelectorBootstrap re-runs WaitForScenarioAndMaterialise on every
        // SC scene entry; the MaterialisedThisSession guard in the bootstrap
        // is unreliable because the ScenarioModule field gets reset between
        // scenes (KSP serialization quirk — log shows "Loaded spec from save:
        // Duna" + "Duna already materialised this session — skipping" on
        // every scene entry, meaning Materialise short-circuits but
        // WaitAndPlace fires anyway). Without this guard, AC flickers
        // L1->L3->L1 on every SC entry as TryForceAcToL3 runs again, and
        // Place may attempt duplicate-clone the FlagPole and Crawlerway.
        private static readonly System.Collections.Generic.HashSet<string>
            _placedForBodies = new System.Collections.Generic.HashSet<string>();

        public static void ResetPlacedTracking()
        {
            _placedForBodies.Clear();
        }

        // Single source of truth for the "wait-then-place" sequence.  Both
        // the SpaceCenter bootstrap and the AP-connect path host this on
        // their own MonoBehaviours.
        public static IEnumerator WaitAndPlace(BodySpec spec, CelestialBody body, GroupCenter groupCenter)
        {
            if (_placedForBodies.Contains(spec.Name))
            {
                // Already placed this session — no need to force AC to L3
                // again. Prevents the AC mesh flicker on VAB/flight return.
                yield break;
            }

            // FlagPole/Crawlerway decoration sources come from the live
            // Kerbin KSC scene (FindACFlagSource at Decorations.cs requires
            // the AC's currently-active facilityInstance to be the L3 prefab
            // with the model01/Pole hierarchy). If the player has downgraded
            // the AC (e.g. via AP CareerUpgradesManager), the source meshes
            // for L3-only decorations vanish and AreSourcesReady never
            // returns true.
            //
            // We temporarily force AC to L3 here so the existing decoration
            // pipeline (unchanged) finds its sources, then restore after
            // Place(). Validated mechanism — see notes/career_spike.md.
            // SuppressPostRevert prevents CareerUpgradesManager's POST-revert
            // observer from fighting the temporary SetLevel.
            var (acFacility, origLevel) = TryForceAcToL3();
            try
            {
                int waited = 0;
                while (!AreSourcesReady() && waited < MaxReadinessFrames)
                {
                    yield return null;
                    waited++;
                }
                if (!AreSourcesReady())
                {
                    Debug.LogError($"[KSPArchipelago.KSC] Decoration sources not ready after " +
                                   $"{MaxReadinessFrames} frames — skipping placement. " +
                                   "Likely caused by AP-connect outside SpaceCenter, or a KSC " +
                                   "scene-hierarchy change in a newer KSP version.");
                    yield break;
                }
                Place(spec, body, groupCenter);
                _placedForBodies.Add(spec.Name);
            }
            finally
            {
                RestoreAcLevel(acFacility, origLevel);
            }
        }

        // Returns (acFacility, origLevel). If the AC isn't findable or
        // is already at L3, no change is made and the caller's finally
        // will detect that via origLevel == -1.
        private static System.Tuple<Upgradeables.UpgradeableFacility, int> TryForceAcToL3()
        {
            Upgradeables.UpgradeableFacility ac = null;
            foreach (var f in Resources.FindObjectsOfTypeAll<Upgradeables.UpgradeableFacility>())
            {
                if (f != null && f.name == "AstronautComplex") { ac = f; break; }
            }
            if (ac == null)
            {
                Debug.LogWarning("[KSPArchipelago.KSC] AC facility not found — "
                               + "cannot force L3 for decoration sources. "
                               + "Place() may skip if AC was downgraded.");
                return System.Tuple.Create<Upgradeables.UpgradeableFacility, int>(null, -1);
            }
            int origLevel = ac.FacilityLevel;
            if (origLevel >= 2)
            {
                // Already at L3 (internal idx 2 = max) — no change needed,
                // signal "nothing to restore" via origLevel = -1.
                return System.Tuple.Create<Upgradeables.UpgradeableFacility, int>(ac, -1);
            }
            try
            {
                var mgr = KSPArchipelago.CareerUpgradesManager.Instance;
                // Tell POST-revert to ignore facility-upgrade events for the
                // AC until we see newLvl == origLevel (the post-restore state).
                // That naturally covers both the intermediate L3 and the
                // restore event, regardless of KSP's async coroutine timing.
                if (mgr != null) mgr.ExpectStableLevel("SpaceCenter/AstronautComplex", origLevel);
                ac.SetLevel(2);
                Debug.Log($"[KSPArchipelago.KSC] Forced AC L{origLevel + 1} -> L3 "
                        + "for decoration source extraction");
                return System.Tuple.Create(ac, origLevel);
            }
            catch (Exception ex)
            {
                Debug.LogError("[KSPArchipelago.KSC] AC SetLevel(2) failed: " + ex);
                var mgr = KSPArchipelago.CareerUpgradesManager.Instance;
                if (mgr != null) mgr.ClearExpectedStableLevel("SpaceCenter/AstronautComplex");
                return System.Tuple.Create<Upgradeables.UpgradeableFacility, int>(null, -1);
            }
        }

        private static void RestoreAcLevel(Upgradeables.UpgradeableFacility ac, int origLevel)
        {
            // Note: the matching ExpectStableLevel(origLevel) was set in
            // TryForceAcToL3 — POST-revert auto-clears when it observes the
            // restore event. If no SetLevel runs here (origLevel was -1 or
            // ac was null), the expectation entry won't auto-clear; force
            // clear in the finally below to avoid leaking suppression state.
            try
            {
                if (ac != null && origLevel >= 0)
                {
                    ac.SetLevel(origLevel);
                    Debug.Log($"[KSPArchipelago.KSC] Restored AC -> L{origLevel + 1}");
                }
                else
                {
                    var mgr = KSPArchipelago.CareerUpgradesManager.Instance;
                    if (mgr != null) mgr.ClearExpectedStableLevel("SpaceCenter/AstronautComplex");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[KSPArchipelago.KSC] AC SetLevel restore failed: " + ex);
                var mgr = KSPArchipelago.CareerUpgradesManager.Instance;
                if (mgr != null) mgr.ClearExpectedStableLevel("SpaceCenter/AstronautComplex");
            }
        }

        // Deterministic readiness check for the scene state Decorations.Place
        // needs.  Replaces an arbitrary WaitForSeconds(2) inherited from
        // AP_KSC_Probe: each lookup is a concrete precondition, and renderer
        // bounds being non-degenerate guards against Unity not having
        // computed bounds yet on freshly-activated meshes.
        public static bool AreSourcesReady()
        {
            // Kerbin LaunchPad must be findable with a valid gameObject —
            // the source of the reference frame in TryBuildKerbinPadFrame.
            Upgradeables.UpgradeableFacility launchPad = null;
            foreach (var f in Resources.FindObjectsOfTypeAll<Upgradeables.UpgradeableFacility>())
            {
                if (f != null && f.name == "LaunchPad") { launchPad = f; break; }
            }
            if (launchPad == null || launchPad.gameObject == null) return false;

            // Crawlerway source: active GameObject with at least one renderer
            // whose bounds Unity has actually computed.
            GameObject crawlerway = FindSource(new[] { "Crawlerway" });
            if (crawlerway == null || !crawlerway.activeInHierarchy) return false;
            if (!HasNonDegenerateBounds(crawlerway, excludeSkinned: false)) return false;

            // AC flag: live level-3 facility prefab.  FindACFlagSource
            // already requires activeInHierarchy + the model01/Pole
            // hierarchy.  We additionally require renderer bounds —
            // excluding SkinnedMeshRenderer, since MeasureSource does too
            // (the cloth Flag's bounds are animation-inflated and irrelevant).
            GameObject flag = FindACFlagSource();
            if (flag == null) return false;
            if (!HasNonDegenerateBounds(flag, excludeSkinned: true)) return false;

            // FlagPoleAdditionalCollider is instantiated by Squad scene-init
            // code (not present in the scene file), so it can lag the other
            // GameObjects by several frames.
            bool triggerActive = false;
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null) continue;
                if (!go.name.StartsWith("FlagPoleAdditionalCollider")) continue;
                if (!go.activeInHierarchy) continue;
                triggerActive = true;
                break;
            }
            if (!triggerActive) return false;

            return true;
        }

        private static bool HasNonDegenerateBounds(GameObject src, bool excludeSkinned)
        {
            Renderer[] rends = src.GetComponentsInChildren<Renderer>(true);
            if (rends.Length == 0) return false;
            foreach (Renderer r in rends)
            {
                if (r == null) continue;
                if (excludeSkinned && r is SkinnedMeshRenderer) continue;
                if (r.bounds.size.sqrMagnitude > 0.0001f) return true;
            }
            return false;
        }

        public static void Place(BodySpec spec, CelestialBody body, GroupCenter groupCenter)
        {
            CelestialBody kerbin = FlightGlobals.Bodies.Find(b => b.name == "Kerbin");
            if (kerbin == null)
            {
                Debug.LogError("[KSPArchipelago.KSC] Decorations: Kerbin not found");
                return;
            }

            if (!TryBuildKerbinPadFrame(kerbin, out Vector3 padWorld,
                out Vector3d padEast, out Vector3d padNorth, out Vector3d padUp))
            {
                Debug.LogWarning("[KSPArchipelago.KSC] Decorations: Kerbin LaunchPad not found — skipping");
                return;
            }

            // Build the alien GroupCenter's tangent frame.  We measure
            // the GroupCenter's actual forward heading at runtime (as
            // the probe does) because PQSCity orientation can drift
            // slightly from the cfg-time RefLongitude on non-Kerbin
            // bodies, and using transform.forward keeps the placement
            // consistent with KK's actual placement of buildings.
            GameObject groupGO = groupCenter.gameObject;
            body.GetLatLonAlt(groupGO.transform.position, out double gLat, out double gLon, out _);
            Vector3d gUp = body.GetSurfaceNVector(gLat, gLon).normalized;
            Vector3d gNorth = (body.GetSurfaceNVector(gLat + 0.001, gLon)
                              - body.GetSurfaceNVector(gLat, gLon)).normalized;
            gNorth = (gNorth - gUp * Vector3d.Dot(gNorth, gUp)).normalized;
            Vector3d gEast = Vector3d.Cross(gUp, gNorth).normalized;
            Vector3d gFwd = (Vector3d) groupGO.transform.forward;
            double groupHeading =
                Math.Atan2(Vector3d.Dot(gFwd, gEast), Vector3d.Dot(gFwd, gNorth)) * 180.0 / Math.PI;
            if (groupHeading < 0) groupHeading += 360.0;
            double effectiveLon = groupHeading - 90.0;
            double L = (effectiveLon + 90.0) * Math.PI / 180.0;
            double cosL = Math.Cos(L);
            double sinL = Math.Sin(L);

            int placed = 0;
            foreach (var d in DECORATIONS)
            {
                // FlagPole_AC has a deterministic source lookup (walk
                // the AstronautComplex UpgradeableObject's active level
                // instance to find its flag-mesh wrapper).  The probe's
                // name-based search matched flagPole_lev1 GameObjects
                // inside Administration upgrade prefabs (always inactive
                // in level-3 sandbox) — never the live AC flag.
                GameObject src = d.Name == "FlagPole_AC"
                    ? FindACFlagSource()
                    : FindSource(d.SearchNames);
                if (src == null)
                {
                    Debug.LogWarning($"[KSPArchipelago.KSC] Decorations: source not found for {d.Name}");
                    continue;
                }

                // Measure the source's visible mesh and place it relative
                // to Kerbin's LaunchPad in (east, north, up) coords.
                if (!MeasureSource(src, d, padWorld, padEast, padNorth, padUp,
                    out double srcHeading, out Vector3 meshLocalOffset, out Vector3d measuredENU))
                {
                    Debug.LogWarning($"[KSPArchipelago.KSC] Decorations: source has no renderers for {d.Name}");
                    continue;
                }

                double targetEast  = d.UseExplicitPosition ? d.East  : measuredENU.x;
                double targetNorth = d.UseExplicitPosition ? d.North : measuredENU.y;
                double targetUp    = d.UseExplicitPosition ? d.Up    : measuredENU.z;

                double eastW  = targetEast  - BodyData.CLUSTER_OFFSET_EAST;
                double northW = targetNorth - BodyData.CLUSTER_OFFSET_NORTH;
                double xVisual = cosL * eastW - sinL * northW;
                double zVisual = sinL * eastW + cosL * northW;

                // Anchor-base decorations skip BUILDING_LIFT_M (their visible
                // base IS the pad surface; lifting would float them).
                double effectiveLift = d.AnchorBase ? 0.0 : BodyData.BUILDING_LIFT_M;
                float yVisual = (float)(targetUp + effectiveLift);

                double corrected = ((srcHeading - groupHeading) % 360.0 + 360.0) % 360.0;

                // Back-compute pivot: visual_center = pivot + rotation * meshLocalOffset
                Quaternion oriQ = Quaternion.Euler(0f, (float) corrected, 0f);
                Vector3 rotatedOffset = oriQ * meshLocalOffset;
                Vector3 pivotLocal = new Vector3(
                    (float) xVisual - rotatedOffset.x,
                    yVisual          - rotatedOffset.y,
                    (float) zVisual - rotatedOffset.z);

                // Capture the source's WORLD-space scale before we
                // detach.  When the source lives deep in a hierarchy
                // (Mesh/Facility/Building has non-1 ancestor scales),
                // the clone's localScale only encodes the bottom-most
                // multiplier — reparenting it under our groupGO would
                // drop the ancestor multipliers and render at the
                // wrong size.
                Vector3 srcLossy = src.transform.lossyScale;

                GameObject clone = UnityEngine.Object.Instantiate(src);
                // Keep "FlagPole" as the GameObject name for the AC flag
                // clone — KSP's biome lookup is keyed by nearby
                // GameObject names (FlagPole → "FlagPole" biome).
                clone.name = d.Name == "FlagPole_AC"
                    ? "FlagPole"
                    : $"APKSC_{spec.Name}_{d.Name}";
                clone.transform.parent = groupGO.transform;
                Vector3 parentLossy = groupGO.transform.lossyScale;
                clone.transform.localScale = new Vector3(
                    srcLossy.x / parentLossy.x,
                    srcLossy.y / parentLossy.y,
                    srcLossy.z / parentLossy.z);
                clone.transform.localPosition    = pivotLocal;
                clone.transform.localEulerAngles = new Vector3(0f, (float) corrected, 0f);
                clone.SetActive(true);
                placed++;
                Debug.Log($"[KSPArchipelago.KSC] Placed {d.Name} on {spec.Name} pivot=({pivotLocal.x:F1},{pivotLocal.y:F2},{pivotLocal.z:F1}) ori_y={corrected:F1}°");

                // The FlagPole biome trigger lives in a separate GameObject
                // ("FlagPoleAdditionalCollider(Clone)", sibling of the
                // visible Building mesh under Mesh/) — it's a trigger
                // collider Squad instantiates at runtime and what
                // ScienceUtil checks for FlagPole biome attribution.
                // Clone it onto our visible flag so the trigger travels
                // with the flag at the right world position.
                if (d.Name == "FlagPole_AC")
                {
                    AttachFlagPoleTrigger(clone, srcLossy);
                }
            }
            Debug.Log($"[KSPArchipelago.KSC] Decorations: {placed} placed");
        }

        private static void AttachFlagPoleTrigger(GameObject flagClone, Vector3 srcLossy)
        {
            GameObject trigger = null;
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null) continue;
                if (!go.name.StartsWith("FlagPoleAdditionalCollider")) continue;
                if (!go.activeInHierarchy) continue;
                trigger = go;
                break;
            }
            if (trigger == null)
            {
                Debug.LogWarning("[KSPArchipelago.KSC] FlagPoleAdditionalCollider not found in scene — no biome trigger placed");
                return;
            }

            Vector3 triggerLossy = trigger.transform.lossyScale;
            GameObject triggerClone = UnityEngine.Object.Instantiate(trigger);
            triggerClone.name = "FlagPoleAdditionalCollider";
            triggerClone.transform.parent = flagClone.transform;
            // Preserve trigger's world-space scale through the new parent.
            Vector3 flagLossy = flagClone.transform.lossyScale;
            triggerClone.transform.localScale = new Vector3(
                triggerLossy.x / flagLossy.x,
                triggerLossy.y / flagLossy.y,
                triggerLossy.z / flagLossy.z);
            triggerClone.transform.localPosition    = Vector3.zero;
            triggerClone.transform.localEulerAngles = Vector3.zero;
            triggerClone.SetActive(true);
            Debug.Log("[KSPArchipelago.KSC] Attached FlagPoleAdditionalCollider to flag clone");
        }

        // Deterministic AC flagpole lookup.  Walks every
        // UpgradeableObject named "AstronautComplex" and returns the
        // "Building" child of its currently-active facilityInstance —
        // that's where Squad puts the flagpole mesh (Pole, FlagMount*,
        // Flag, and the joints01 cloth-sim siblings) for AC level-3,
        // verified empirically against the live SpaceCenter scene.
        // No name-based guessing through inactive upgrade prefabs.
        // Find the live FlagPole mesh source by direct hierarchy match.
        // The previous diagnostic dump captured the visible AC flagpole
        // at "SpaceCenter/Mesh/Facility/Building/model01/Pole" — a
        // "Facility" GameObject under "Mesh" that's a sibling of the
        // AC's own facility instance (which has a lowercase 'building'
        // child for the AC building proper).  Squad/KSP uses the same
        // generic name "Facility" for multiple facility wrappers under
        // "Mesh"; the flagpole wrapper is identified by holding a
        // capital-B "Building" child that contains model01/Pole.
        private static GameObject FindACFlagSource()
        {
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || go.name != "Building") continue;
                if (!go.activeInHierarchy) continue;
                if (go.transform.Find("model01/Pole") == null) continue;
                // Verify the ancestry: this should sit under SpaceCenter/Mesh/Facility
                // so we don't grab a similarly-shaped GameObject from elsewhere.
                Transform p = go.transform.parent;
                if (p == null || p.name != "Facility") continue;
                if (p.parent == null || p.parent.name != "Mesh") continue;
                return go;
            }
            return null;
        }

        // Same lookup as AP_KSC_Probe.RegisterSource — first candidate
        // by name with renderers — but with one robustness tweak: prefer
        // candidates that are active in the hierarchy.  Reason: KSP's
        // KSC scene contains the same GameObject name (e.g.,
        // "flagPole_lev1") inside multiple upgrade-level prefabs;
        // only the current level's copy is active.  Resources
        // .FindObjectsOfTypeAll iteration order isn't guaranteed, so
        // the probe was getting the active one by accident.  We pick
        // it deterministically.
        private static GameObject FindSource(string[] searchNames)
        {
            GameObject inactiveFallback = null;
            foreach (string name in searchNames)
            {
                foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (candidate.name != name) continue;
                    if (candidate.GetComponentsInChildren<Renderer>(true).Length == 0) continue;
                    if (candidate.activeInHierarchy) return candidate;
                    if (inactiveFallback == null) inactiveFallback = candidate;
                }
            }
            return inactiveFallback;
        }

        private static bool TryBuildKerbinPadFrame(CelestialBody kerbin,
            out Vector3 padWorld, out Vector3d padEast, out Vector3d padNorth, out Vector3d padUp)
        {
            padWorld = Vector3.zero;
            padEast = padNorth = padUp = Vector3d.zero;

            Upgradeables.UpgradeableFacility launchPad = null;
            foreach (var f in Resources.FindObjectsOfTypeAll<Upgradeables.UpgradeableFacility>())
            {
                if (f.name == "LaunchPad") { launchPad = f; break; }
            }
            if (launchPad == null || launchPad.gameObject == null) return false;

            padWorld = launchPad.gameObject.transform.position;
            kerbin.GetLatLonAlt(padWorld, out double padLat, out double padLon, out _);
            padUp = kerbin.GetSurfaceNVector(padLat, padLon).normalized;
            padNorth = (kerbin.GetSurfaceNVector(padLat + 0.001, padLon)
                       - kerbin.GetSurfaceNVector(padLat, padLon)).normalized;
            padNorth = (padNorth - padUp * Vector3d.Dot(padNorth, padUp)).normalized;
            padEast = Vector3d.Cross(padUp, padNorth).normalized;
            return true;
        }

        private static bool MeasureSource(GameObject src, DecorationSpec spec,
            Vector3 padWorld, Vector3d padEast, Vector3d padNorth, Vector3d padUp,
            out double headingDeg, out Vector3 meshLocalOffset, out Vector3d measuredENU)
        {
            headingDeg = 0;
            meshLocalOffset = Vector3.zero;
            measuredENU = Vector3d.zero;

            // Source forward heading in Kerbin pad ENU frame
            Vector3d fwd = (Vector3d) src.transform.forward;
            double h = Math.Atan2(Vector3d.Dot(fwd, padEast), Vector3d.Dot(fwd, padNorth)) * 180.0 / Math.PI;
            if (h < 0) h += 360.0;
            headingDeg = h;

            // For AnchorBase, exclude SkinnedMeshRenderer (cloth Flag etc).
            // Its bounds are inflated for animation and would push AABB.min.y
            // below the visible base of the rigid mesh.
            Renderer[] rends = src.GetComponentsInChildren<Renderer>(true);
            if (rends.Length == 0) return false;
            Renderer[] rigid = spec.AnchorBase
                ? Array.FindAll(rends, r => !(r is SkinnedMeshRenderer))
                : rends;
            if (rigid.Length == 0) rigid = rends;

            Bounds bounds = rigid[0].bounds;
            for (int i = 1; i < rigid.Length; i++) bounds.Encapsulate(rigid[i].bounds);

            Vector3 pivot = src.transform.position;
            Vector3 visualTarget = bounds.center;
            if (spec.AnchorBase)
            {
                // Lowest AABB corner along padUp = visible base.
                Vector3 c = bounds.center, e = bounds.extents;
                double minProj = double.MaxValue;
                Vector3 baseCorner = c;
                for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 corner = c + new Vector3(sx * e.x, sy * e.y, sz * e.z);
                    double proj = Vector3d.Dot((Vector3d) (corner - padWorld), padUp);
                    if (proj < minProj) { minProj = proj; baseCorner = corner; }
                }
                visualTarget = new Vector3(bounds.center.x, baseCorner.y, bounds.center.z);
            }

            Vector3 deltaWorld = visualTarget - pivot;
            meshLocalOffset = src.transform.InverseTransformDirection(deltaWorld);

            Vector3d toTarget = (Vector3d) (visualTarget - padWorld);
            double vEast  = Vector3d.Dot(toTarget, padEast);
            double vNorth = Vector3d.Dot(toTarget, padNorth);
            double vUp    = Vector3d.Dot(toTarget, padUp);
            // AnchorBase overrides vUp=0 because we want the base on the
            // alien pad surface, not preserving Kerbin's slab offset.
            if (spec.AnchorBase) vUp = 0.0;
            measuredENU = new Vector3d(vEast, vNorth, vUp);
            return true;
        }
    }
}
