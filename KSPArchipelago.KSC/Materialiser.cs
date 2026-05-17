// Runtime spawn pipeline for the chosen body's APKSC group.
//
// Mirrors AP_KSC_Sites/generate.py's per-body cfgs, but built in
// memory and applied to the live game.  Idempotent within a KSP
// session: re-firing on the same body is a no-op.  Switching to a
// different body requires a KSP restart (dematerialisation is not
// implemented for the phase 2 PoC).
//
// Group naming caveat: API.PlaceStatic has a bug where the group
// name argument is ignored unless an already-existing GroupCenter
// matches it by *unqualified* name (whereas all GroupCenter keys
// are body-qualified).  The instances we place therefore end up in
// the body's "SaveGame" group instead of "APKSC".  Functionally
// equivalent — KK doesn't care about group names beyond their dbKey
// — but the group will show as "Mun_SaveGame" in KK debug UIs.
// Phase 3 may patch this upstream.

using System;
using System.Collections.Generic;
using System.Reflection;
using CommNet;
using KerbalKonstructs;
using KerbalKonstructs.Core;
using UnityEngine;

namespace KSPArchipelago.KSC
{
    // Compile-time guard for CommNetHome's protected fields.
    // The `this.X` references make a future KSP rename fail at build
    // time instead of silently no-oping at runtime.  Never
    // instantiated; existing solely as a sentinel.
    internal class APCommNetHomeFieldGuard : CommNetHome
    {
        private void _AssertFields()
        {
            CelestialBody _b = this.body;
            double _la = this.lat;
            double _lo = this.lon;
            double _al = this.alt;
            Transform _nt = this.nodeTransform;
        }
    }

    public static class Materialiser
    {
        // Set after a successful Materialise call.  null until first
        // materialisation in this KSP session.
        public static string CurrentBody = null;

        // The GroupCenter our buildings were placed under.  Used by
        // the bootstrap coroutine to drive decoration placement after
        // a delay (see SelectorBootstrap.WaitForScenarioAndMaterialise).
        public static GroupCenter LastGroupCenter = null;
        public static BodySpec LastSpec;

        private static bool _onLaunchHooked = false;

        public static void Materialise(BodySpec spec)
        {
            if (spec.Name == "Kerbin")
            {
                RestoreStockKerbinLaunchSites();
                CurrentBody = "Kerbin";
                return;
            }

            if (CurrentBody == spec.Name)
            {
                Debug.Log($"[KSPArchipelago.KSC] {spec.Name} already materialised this session — skipping.");
                return;
            }

            if (CurrentBody != null && CurrentBody != spec.Name)
            {
                // Mid-session body switch is unsupported — dematerialising
                // the previous body's statics is non-trivial and we
                // explicitly punted on it.  But we can't just leave the
                // picker in the previous body's state: by the time the
                // new save's editor scene loads, the previous body's
                // alien LaunchPad/Runway aren't there to launch from,
                // stock Kerbin pads are still hidden, and the player
                // ends up with zero valid launch sites.
                //
                // Restore stock Kerbin pads as a launch fallback and
                // tell the player they need to restart KSP.
                Debug.LogError(
                    $"[KSPArchipelago.KSC] Cannot switch from {CurrentBody} to {spec.Name} mid-session. " +
                    "Restoring Kerbin pads — restart KSP to use this save's starting body.");
                RestoreStockKerbinLaunchSites();
                MethodInfo setupValid = typeof(EditorDriver).GetMethod(
                    "setupValidLaunchSites",
                    BindingFlags.NonPublic | BindingFlags.Static);
                setupValid?.Invoke(null, null);
                ScreenMessages.PostScreenMessage(
                    $"KSPArchipelago.KSC: cannot switch starting body ({CurrentBody} → {spec.Name}) " +
                    "mid-session. Quit to main menu and restart KSP to use this save.",
                    15f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }

            CelestialBody body = FlightGlobals.GetBodyByName(spec.Name);
            if (body == null)
            {
                Debug.LogError($"[KSPArchipelago.KSC] Body not found: {spec.Name}");
                return;
            }

            Debug.Log($"[KSPArchipelago.KSC] Materialising APKSC on {spec.Name} (lat={spec.Lat}, lon={spec.Lon}, alt={spec.TerrainAltM})");

            // Apply the MapDecal first so the PQS terrain is flattened to
            // spec.TerrainAltM by the time PQSCity tries to position the
            // GroupCenter on the surface.
            if (!spec.SkipMapDecal)
            {
                PlaceMapDecal(spec, body);
            }

            GroupCenter groupCenter = CreateGroupCenter(spec, body);
            PlaceFacilities(spec, body, groupCenter);
            // Decorations are placed AFTER a delay by the bootstrap
            // coroutine, mirroring AP_KSC_Probe's WaitForSeconds(2).
            // Source GameObjects (Crawlerway, flagPole_lev1) aren't
            // necessarily in memory at scene-init time, and renderer
            // bounds on inactive prefab objects are unstable until
            // Unity has had a chance to compute them.  Stash the
            // GroupCenter so the bootstrap can drive the decoration
            // pass once the scene settles.
            LastGroupCenter = groupCenter;
            LastSpec = spec;

            RegisterAlienLaunchSites(spec);
            CurrentBody = spec.Name;     // HideKerbinLaunchSites reads this
            HideKerbinLaunchSites();
            // SetDefaultLaunchSite is deferred to EditorLaunchGate — calling
            // it here NREs because LaunchSiteManager.setLaunchSite writes to
            // EditorLogic.fetch.launchSiteName, and EditorLogic only exists
            // in the VAB/SPH scene (we're in SpaceCenter right now).
            HookOnLaunchDefense();

            FlipHomeFlag(body);
            RelocateDSN(spec, body);

            Debug.Log($"[KSPArchipelago.KSC] Materialisation complete for {spec.Name}");
        }

        // Reassign KSP's home-body designation to the alien body.
        // Mirrors the sequence in KerbalKonstructs.FuckUpKSP (KK
        // commented out the call site, so we run the same operations
        // ourselves).  Without this, KSP keeps Kerbin as the home
        // world: recovery is blocked on the alien LaunchPad, and
        // onReturnFromOrbit / onReturnFromSurface never fire when a
        // vessel lands on the alien body.  (DSN is anchored
        // separately via CommNetHome — see RelocateDSN.)
        //
        // Public so SelectorScenarioModule.OnLoad can call it at
        // every scene transition — earlier than the SpaceCenter
        // bootstrap path, so the TrackingStation scene's camera
        // init sees the right home flag.
        public static void FlipHomeFlag(CelestialBody newHome)
        {
            CelestialBody oldHome = null;
            foreach (CelestialBody b in FlightGlobals.Bodies)
            {
                if (b != null && b.isHomeWorld) { oldHome = b; break; }
            }
            bool alreadyCorrect = (oldHome == newHome);
            string kspHome = Planetarium.fetch?.Home?.name ?? "<null>";
            bool planetariumAlreadyCorrect = (kspHome == newHome.name);

            if (alreadyCorrect && planetariumAlreadyCorrect)
            {
                Debug.Log($"[KSPArchipelago.KSC] FlipHomeFlag: no-op — {newHome.name} already home " +
                          $"(isHomeWorld + Planetarium.fetch.Home both already set)");
                return;
            }

            if (oldHome != null && oldHome != newHome)
            {
                oldHome.isHomeWorld = false;
            }
            newHome.isHomeWorld = true;
            Planetarium.fetch.Home = newHome;

            // Invalidate FlightGlobals's home-body cache.  GetHomeBody() and
            // GetHomeBodyName() lazily scan isHomeWorld once and stash the
            // result in fetch.homeBody — if KSP populated that cache before
            // our flip, subsequent callers (keypress-reset, waypoint defaults,
            // comet spawner labels, etc.) keep returning the stale Kerbin
            // reference.
            ClearFlightGlobalsHomeCache();

            Debug.Log($"[KSPArchipelago.KSC] Home flipped: {oldHome?.name ?? "<none>"} → {newHome.name} " +
                      $"(isHomeWorld_was_correct={alreadyCorrect}, " +
                      $"Planetarium_was_correct={planetariumAlreadyCorrect})");
        }

        private static void ClearFlightGlobalsHomeCache()
        {
            FlightGlobals fg = FlightGlobals.fetch;
            if (fg == null) return;
            const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo hbFld  = typeof(FlightGlobals).GetField("homeBody",      BF);
            FieldInfo hbiFld = typeof(FlightGlobals).GetField("homeBodyIndex", BF);
            if (hbFld == null || hbiFld == null)
            {
                Debug.LogWarning("[KSPArchipelago.KSC] ClearFlightGlobalsHomeCache: " +
                                 $"fields not found (homeBody={hbFld != null}, " +
                                 $"homeBodyIndex={hbiFld != null})");
                return;
            }
            hbFld.SetValue(fg, null);
            hbiFld.SetValue(fg, 0);
        }

        // Move the KSC's CommNetHome (the one with isKSC == true) from
        // Kerbin to the alien body.  CommNet's DSN anchor is independent
        // of Planetarium.fetch.Home — flipping that flag alone leaves
        // the radio range computing against Kerbin's KSC position.
        //
        // The position source-of-truth is `nodeTransform.position`, not
        // the body/lat/lon fields (those start at (0,0) on the stock
        // KSC CommNetHome — verified from prior session logs).  We
        // swap nodeTransform to the alien TrackingStation's transform
        // and also overwrite body/lat/lon defensively in case any
        // KSP code path consults them.  Field names verified at
        // compile time by APCommNetHomeFieldGuard.
        private static void RelocateDSN(BodySpec spec, CelestialBody newHome)
        {
            CommNetHome ksc = null;
            foreach (CommNetHome h in UnityEngine.Object.FindObjectsOfType<CommNetHome>())
            {
                if (h != null && h.isKSC) { ksc = h; break; }
            }
            if (ksc == null)
            {
                Debug.LogWarning("[KSPArchipelago.KSC] RelocateDSN: stock KSC CommNetHome not found");
                return;
            }

            Transform alienTS = FindAlienTrackingStationTransform(newHome);
            if (alienTS == null)
            {
                Debug.LogWarning("[KSPArchipelago.KSC] RelocateDSN: alien TrackingStation not " +
                                 "found in StaticDatabase — DSN node-transform unchanged");
            }

            var bodyAcc = FindMember(typeof(CommNetHome), "body");
            var latAcc  = FindMember(typeof(CommNetHome), "lat");
            var lonAcc  = FindMember(typeof(CommNetHome), "lon");
            var altAcc  = FindMember(typeof(CommNetHome), "alt");
            var ntAcc   = FindMember(typeof(CommNetHome), "nodeTransform");
            if (bodyAcc == null || latAcc == null || lonAcc == null
                || altAcc == null || ntAcc == null)
            {
                Debug.LogError("[KSPArchipelago.KSC] RelocateDSN: member reflection failed " +
                               $"(body={bodyAcc != null}, lat={latAcc != null}, " +
                               $"lon={lonAcc != null}, alt={altAcc != null}, " +
                               $"nodeTransform={ntAcc != null})");
                return;
            }

            CelestialBody oldBody = (CelestialBody) bodyAcc.Get(ksc);
            double oldLat = (double) latAcc.Get(ksc);
            double oldLon = (double) lonAcc.Get(ksc);
            Transform oldNT = (Transform) ntAcc.Get(ksc);
            string oldNTPos = oldNT != null ? oldNT.position.ToString("F0") : "<null>";

            bool alreadyCorrect = oldBody == newHome
                && Math.Abs(oldLat - spec.Lat) < 0.0001
                && Math.Abs(oldLon - spec.Lon) < 0.0001
                && (alienTS == null || oldNT == alienTS);
            if (alreadyCorrect)
            {
                Debug.Log($"[KSPArchipelago.KSC] RelocateDSN: no-op — KSC CommNetHome already " +
                          $"on {newHome.name} (nodeTransform unchanged)");
                return;
            }

            bodyAcc.Set(ksc, newHome);
            latAcc.Set(ksc, spec.Lat);
            lonAcc.Set(ksc, spec.Lon);
            altAcc.Set(ksc, spec.TerrainAltM);
            if (alienTS != null) ntAcc.Set(ksc, alienTS);
            CommNetNetwork.Reset();
            string newNTPos = alienTS != null
                ? alienTS.position.ToString("F0")
                : oldNTPos + " (unchanged)";
            Debug.Log($"[KSPArchipelago.KSC] DSN relocated: " +
                      $"{oldBody?.name ?? "<null>"} (lat={oldLat:F4}, lon={oldLon:F4}, " +
                      $"nt@{oldNTPos}) → " +
                      $"{newHome.name} (lat={spec.Lat:F4}, lon={spec.Lon:F4}, nt@{newNTPos})");
        }

        // Field-or-property accessor that uniformly walks the inheritance
        // hierarchy.  KSP API objects often expose state via a public
        // property with a private backing field; either shape is fine.
        private class MemberAccess
        {
            public FieldInfo Field;
            public PropertyInfo Prop;
            public object Get(object target)
                => Field != null ? Field.GetValue(target) : Prop.GetValue(target, null);
            public void Set(object target, object value)
            {
                if (Field != null) Field.SetValue(target, value);
                else                 Prop.SetValue(target, value, null);
            }
        }

        private static MemberAccess FindMember(Type t, string name)
        {
            const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public
                                  | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            for (Type cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                FieldInfo f = cur.GetField(name, BF);
                if (f != null) return new MemberAccess { Field = f };
                PropertyInfo p = cur.GetProperty(name, BF);
                if (p != null && p.CanRead && p.CanWrite) return new MemberAccess { Prop = p };
            }
            return null;
        }

        // Locate the alien TrackingStation building's gameObject transform.
        // PlaceFacilities placed it as a StaticInstance on `body` using
        // model name "KSC_TrackingStation_level_3"; gameObject.name is
        // assigned by KK as `{groupname}_{modelname}_{idx}`, so we match
        // on substring.
        private static Transform FindAlienTrackingStationTransform(CelestialBody body)
        {
            const string MODEL = "KSC_TrackingStation_level_3";
            foreach (StaticInstance inst in StaticDatabase.GetAllStatics())
            {
                if (inst == null || inst.CelestialBody == null) continue;
                if (inst.CelestialBody.name != body.name) continue;
                if (inst.gameObject == null) continue;
                if (inst.gameObject.name != null && inst.gameObject.name.Contains(MODEL))
                {
                    return inst.gameObject.transform;
                }
            }
            return null;
        }

        // 1. GroupCenter at the cluster centre.  Heading=361 selects KK's
        // legacy orientation path (GroupCenter.UpdateRotation2Heading,
        // GroupCenter.cs:166), so pqsCity.reorientFinalAngle = our
        // RotationAngle (=0).  SeaLevelAsReference=false leaves PQSCity's
        // repositionToSphereSurface=true (GroupCenter.SetReference) so
        // the group rides the body's surface — exactly what we need for
        // buildings to end up at the right altitude regardless of
        // per-lat/lng terrain variance.
        private static GroupCenter CreateGroupCenter(BodySpec spec, CelestialBody body)
        {
            GroupCenter gc = new GroupCenter
            {
                Group               = "APKSC",
                CelestialBody       = body,
                RefLatitude         = spec.Lat,
                RefLongitude        = spec.Lon,
                // Equivalent to KKMath.GetRadiadFromLatLng (which is
                // internal): body-local unit vector toward (lat, lng)
                // scaled by the body's radius.
                RadialPosition      = body.GetRelSurfaceNVector(spec.Lat, spec.Lon).normalized * (float)body.Radius,
                Heading             = 361f,   // legacy mode
                RotationAngle       = 0f,
                SeaLevelAsReference = false,
                RadiusOffset        = 0f,
                ModelScale          = 1f,
            };
            Reflection.GroupCenterSpawn(gc);
            return gc;
        }

        // 2. Spawn 9 StaticInstances with non-zero RelativePosition so
        // KK takes the non-Legacy spawn path — buildings parented to the
        // GroupCenter PQSCity, terrain-following, no per-building
        // surfaceHeight queries.  Mirrors generate.py:gen_static exactly.
        private static void PlaceFacilities(BodySpec spec, CelestialBody body, GroupCenter gc)
        {
            // GroupCenter local frame at (lat=0, lon=L) has +Z at world
            // heading (L+90).  generate.py rotates (east, north) into
            // local (X, Z) via this same L.
            double L = (spec.Lon + 90.0) * Math.PI / 180.0;
            double cosL = Math.Cos(L);
            double sinL = Math.Sin(L);

            foreach (var f in BodyData.FACILITIES)
            {
                double eastW  = f.EastM  - BodyData.CLUSTER_OFFSET_EAST;
                double northW = f.NorthM - BodyData.CLUSTER_OFFSET_NORTH;
                float  xLocal = (float)( cosL * eastW - sinL * northW );
                float  zLocal = (float)( sinL * eastW + cosL * northW );
                float  yLift  = (float)( f.UpM + BodyData.BUILDING_LIFT_M );

                // World heading = L + Orientation.y, so the Euler Y
                // compensates for the local-frame rotation.
                double corrected = ((f.HeadingDeg - spec.Lon - 90.0) % 360.0 + 360.0) % 360.0;

                StaticModel model = Reflection.GetModelByName(f.Model);
                if (model == null)
                {
                    Debug.LogError($"[KSPArchipelago.KSC] Model {f.Model} not found in StaticDatabase");
                    continue;
                }

                StaticInstance inst = Reflection.NewStaticInstance();
                inst.CelestialBody     = body;
                inst.Group             = "APKSC";
                inst.RelativePosition  = new Vector3(xLocal, yLift, zLocal);
                inst.Orientation       = new Vector3(0f, (float)corrected, 0f);
                inst.RotationAngle     = 0f;
                inst.VisibilityRange   = 25000f;
                inst.ModelScale        = 1f;
                inst.IsRelativeToTerrain = 0;  // HeightReference.Unset — non-Legacy path
                Reflection.SetStaticInstanceModel(inst, model);

                Reflection.StaticOrientate(inst);
                Reflection.StaticActivate(inst);
            }
        }

        // 3. MapDecal (KerbinCurve heightmap, body-specific deformity).
        // MapDecalInstance has an internal ctor — only reflection point.
        private static void PlaceMapDecal(BodySpec spec, CelestialBody body)
        {
            double rEff = body.Radius + spec.TerrainAltM;
            double deformity = (BodyData.DECAL_RADIUS_M * BodyData.DECAL_RADIUS_M)
                              / (2.0 * rEff) * BodyData.CALIBRATION_MULTIPLIER;

            MapDecalInstance decal = Reflection.NewMapDecalInstance();
            decal.Name               = $"APKSC_{spec.Name}_Pad";
            decal.CelestialBody      = body;
            decal.Latitude           = spec.Lat;
            decal.Longitude          = spec.Lon;
            decal.Radius             = BodyData.DECAL_RADIUS_M;
            decal.HeightMapName      = BodyData.KSC_HEIGHTMAP;
            decal.HeightMapDeformity = deformity;
            decal.UseAbsolut         = true;
            decal.AbsolutOffset      = (float)spec.TerrainAltM;
            decal.Angle              = 0f;
            decal.SmoothHeight       = 0.001f;
            decal.SmoothColor        = 0.3f;
            decal.CullBlack          = true;
            decal.UseAlphaHeightSmoothing = false;
            decal.Order              = 99999;
            decal.Group              = $"APKSC_{spec.Name}";
            decal.Update(doUpdate: true);
        }

        // 4. Register the alien LaunchPad + Runway with PSystemSetup +
        // LaunchSiteManager so the stock VAB/SPH picker sees them.
        // KK does this automatically for cfg-loaded statics, but
        // API.PlaceStatic does not call AttachLaunchSite for runtime
        // spawns.
        private static void RegisterAlienLaunchSites(BodySpec spec)
        {
            RegisterOne(spec, "KSC_LaunchPad_level_3", "LaunchPad",
                "LaunchPad_spawn", SiteType.VAB, LaunchSiteCategory.RocketPad);
            RegisterOne(spec, "KSC_Runway_level_3", "Runway",
                "End09/SpawnPoint", SiteType.SPH, LaunchSiteCategory.Runway);
        }

        private static void RegisterOne(BodySpec spec, string modelName, string siteSuffix,
            string padTransform, SiteType kind, LaunchSiteCategory category)
        {
            StaticInstance inst = FindInstanceOnBody(spec.Name, modelName);
            if (inst == null)
            {
                Debug.LogError($"[KSPArchipelago.KSC] No placed instance for {modelName} on {spec.Name}");
                return;
            }

            CelestialBody body = FlightGlobals.GetBodyByName(spec.Name);
            string siteName = $"{spec.Name} {siteSuffix}";

            KKLaunchSite site = new KKLaunchSite
            {
                LaunchSiteName     = siteName,
                LaunchPadTransform = padTransform,
                LaunchSiteType     = kind,
                sitecategory       = category,
                body               = body,
                staticInstance     = inst,
                LaunchSiteAuthor   = "KSPArchipelago.KSC",
                LaunchSiteDescription = $"{siteSuffix} on {spec.Name}.",
            };
            site.isOpen = true;

            inst.hasLauchSites = true;
            inst.launchSite    = site;

            LaunchSiteManager.RegisterLaunchSite(site);
        }

        // Walk all placed instances and find the one matching (body, model).
        // Our materialiser places exactly one per body per session, so the
        // first match is correct.
        private static StaticInstance FindInstanceOnBody(string bodyName, string modelName)
        {
            foreach (StaticInstance inst in StaticDatabase.GetAllStatics())
            {
                if (inst == null || inst.CelestialBody == null) continue;
                if (inst.CelestialBody.name != bodyName) continue;
                if (inst.gameObject == null) continue;
                // model is internal; gameObject.name is groupname_modelname_idx
                // (set by StaticDatabase.SetNewName).
                if (inst.gameObject.name != null
                    && inst.gameObject.name.Contains(modelName))
                {
                    return inst;
                }
            }
            return null;
        }

        // 5. Whitelist the editor picker down to just our two alien
        // sites.  Hides stock LaunchPad/Runway, KK's KSC2, Making
        // History sites (Woomerang/Desert/Island Airfield), and
        // anything any other mod has registered.  A hostBody-based
        // filter doesn't work — Squad/MH facility entries can have
        // a null hostBody — so we just keep what we explicitly own.
        //
        // For KK-managed sites we also flip isOpen=false, so any
        // later call to LaunchSiteManager.AlterMHSelector (which
        // re-derives editorFacility from isOpen for non-stock
        // entries) keeps them hidden.  Stock LaunchPad/Runway have
        // isOpen tied to recovery and contracts, so they're left
        // open and gated only via editorFacility (KK's
        // RegisterMHLaunchSites explicitly skips their names when
        // re-deriving).
        // Hide every launch option except this body's alien LaunchPad/Runway.
        //
        // There are two parallel launch-site lists in KSP:
        //   - PSystemSetup.SpaceCenterFacilities  — stock LaunchPad/Runway
        //     + KSC2 + our alien sites (registered via KK.RegisterLaunchSite).
        //   - PSystemSetup.LaunchSites            — MH stand-alone sites
        //     (Woomerang, Desert, Island Airfield).  Different LaunchSite
        //     class, classified as "stock" by KSP so RemoveNonStockLaunchSites
        //     is a no-op.
        //
        // For the first list we whitelist by facility name (everything else
        // gets editorFacility = None).  For the second list we just remove
        // every entry — our alien sites are in the first list, not this one.
        // Then we force EditorDriver to re-derive its valid-sites cache.
        public static void HideKerbinLaunchSites()
        {
            if (CurrentBody == null || CurrentBody == "Kerbin") return;

            string alienPad    = $"{CurrentBody} LaunchPad";
            string alienRunway = $"{CurrentBody} Runway";

            foreach (PSystemSetup.SpaceCenterFacility entry in
                     PSystemSetup.Instance.SpaceCenterFacilities)
            {
                if (entry.facilityName == alienPad || entry.facilityName == alienRunway) continue;
                entry.editorFacility = EditorFacility.None;
            }

            List<LaunchSite> launchSites = PSystemSetup.Instance.LaunchSites;
            if (launchSites != null)
            {
                foreach (LaunchSite ls in new List<LaunchSite>(launchSites))
                {
                    if (ls != null) PSystemSetup.Instance.RemoveLaunchSite(ls.launchSiteName);
                }
            }

            // Re-derive the picker's cached valid-sites list.
            MethodInfo setupValid = typeof(EditorDriver).GetMethod(
                "setupValidLaunchSites",
                BindingFlags.NonPublic | BindingFlags.Static);
            setupValid?.Invoke(null, null);
        }

        // Restores stock Kerbin LaunchPad/Runway to their normal editor
        // assignment.  Used when the chosen body is Kerbin.
        private static void RestoreStockKerbinLaunchSites()
        {
            foreach (PSystemSetup.SpaceCenterFacility entry in
                     PSystemSetup.Instance.SpaceCenterFacilities)
            {
                if (entry.facilityName == "LaunchPad")
                    entry.editorFacility = EditorFacility.VAB;
                else if (entry.facilityName == "Runway")
                    entry.editorFacility = EditorFacility.SPH;
            }
        }

        // 6. Default-launch-site selection lives in EditorLaunchGate;
        // LaunchSiteManager.setLaunchSite NREs in SpaceCenter scene.

        // 7. Belt-and-braces: if anything bypasses the picker gate (e.g.,
        // a mod or console command), a launch from stock Kerbin pads
        // on an alien run produces a clear screen warning rather than
        // dumping the player on Kerbin with no way back to AP logic.
        private static void HookOnLaunchDefense()
        {
            if (_onLaunchHooked) return;
            try
            {
                GameEvents.onLaunch.Add(OnLaunchHook);
                _onLaunchHooked = true;
            }
            catch (Exception ex)
            {
                // KSP's EventData<T>.Add can NRE inside EvtDelegate ctor for
                // certain static-method delegates (Target is null and the
                // diagnostic-name builder dereferences it).  Defense hook is
                // belt-and-braces only — the picker gate is the real
                // protection — so swallow and continue.
                Debug.LogWarning($"[KSPArchipelago.KSC] onLaunch defense hook failed: {ex.Message} (picker gate remains active)");
            }
        }

        private static void OnLaunchHook(EventReport report)
        {
            if (CurrentBody == null || CurrentBody == "Kerbin") return;
            string site = EditorLogic.fetch != null ? EditorLogic.fetch.launchSiteName : null;
            if (site == "LaunchPad" || site == "Runway")
            {
                ScreenMessages.PostScreenMessage(
                    $"Stock KSC pads are disabled when starting on {CurrentBody}. " +
                    $"Use the \"{CurrentBody} LaunchPad\" or \"{CurrentBody} Runway\" instead.",
                    8f, ScreenMessageStyle.UPPER_CENTER);
            }
        }

    }
}
