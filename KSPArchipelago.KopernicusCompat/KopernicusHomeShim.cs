// Kopernicus compatibility shim for non-Kerbin starting bodies.
//
// The mod reassigns KSP's home world to the AP-chosen body (see
// KSPArchipelago.KSC/Materialiser.FlipHomeFlag) so recovery works on the
// alien pad and onReturnFromOrbit/onReturnFromSurface fire there. Kopernicus
// assumes the opposite invariant: that FlightGlobals.GetHomeBody() owns the
// stock KSC. Two of its patches dereference that assumption unguarded --
//
//   Patches/SpaceCenterCamera2_Start.cs (prefix, replaces the stock method):
//       CelestialBody homeBody = FlightGlobals.GetHomeBody();
//       PQSCity ksc = homeBody.pqsController.transform.Find("KSC")
//                                           .GetComponent<PQSCity>();
//   RuntimeUtility/PreciseFloatingOrigin.cs (transpiler on PSystemSetup's
//   SetMainMenu / SetSpaceCentre / SetTrackingStation / SetEditor):
//       SetFloatingOriginToKSC() -- same Find("KSC") deref
//
// -- and an alien body's PQS has no "KSC" child, so both throw and the scene
// never finishes loading. Stock KSP does neither: SpaceCenterCamera2.Start
// resolves its PQS from the serialized pqsName string and never calls
// GetHomeBody().
//
// This shim points FlightGlobals' home-body cache at the body that actually
// owns the stock KSC for the duration of those five stock methods, then
// clears it so every other consumer (the tracking station's camera target in
// MapView.Start, R&D trip-log situations, waypoint defaults) still sees the
// real AP home. The window is deliberately tight: MapView.Start runs after
// SetTrackingStation returns and MUST see the AP home, or it targets a
// hidden Kerbin and NREs on FlightGlobals.ActiveVessel.mapObject.
//
// Nothing here touches Kopernicus's own types -- the patches hang off stock
// methods, so a Kopernicus rewrite degrades this to a no-op rather than
// breaking it. The assembly is skipped entirely by KSP's AssemblyLoader when
// Kopernicus or 0Harmony is absent (see the KSPAssemblyDependency attributes),
// which is the normal case.
//
// Patch shape verified against HarmonyKSP 2.2.1.0 under Mono 6.12: a
// Priority.First prefix orders ahead of Kopernicus's, and a finalizer runs on
// all three paths that matter -- original ran, original skipped by a prefix
// returning false, and a prefix throwing -- so the restore is guaranteed
// without a postfix.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

[assembly: KSPAssemblyDependency("0Harmony", 0, 0)]
[assembly: KSPAssemblyDependency("Kopernicus", 1, 0)]

namespace KSPArchipelago.KopernicusCompat
{
    /// <summary>
    /// Installs <see cref="KopernicusHomeShim"/> once per KSP session. Runs
    /// only when Kopernicus and 0Harmony are both present, because an unmet
    /// KSPAssemblyDependency makes AssemblyLoader skip this assembly's types.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class KopernicusCompatBootstrap : MonoBehaviour
    {
        private void Start()
        {
            try
            {
                KopernicusHomeShim.Install();
            }
            catch (Exception ex)
            {
                Debug.LogError("[KSP-AP/KopernicusCompat] shim install failed; "
                             + "a non-Kerbin start will hang the Space Center "
                             + "scene with Kopernicus installed: " + ex);
            }
        }
    }

    internal static class KopernicusHomeShim
    {
        private const string HarmonyId = "com.kspap.kopernicuscompat";

        // Nesting depth of the patched calls. Incremented unconditionally by
        // the prefix and decremented by the finalizer, so the cache is only
        // written on the outermost entry and cleared on the outermost exit
        // even if KSP ever nests two of these methods.
        private static int _depth;

        private static FieldInfo _homeBodyField;
        private static FieldInfo _homeBodyIndexField;
        private static CelestialBody _kscBody;
        private static bool _kscBodyWarned;
        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;

            const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Static;
            var prefix = new HarmonyMethod(typeof(KopernicusHomeShim)
                .GetMethod(nameof(Prefix), BF)) { priority = Priority.First };
            var finalizer = new HarmonyMethod(typeof(KopernicusHomeShim)
                .GetMethod(nameof(Finalizer), BF));

            var harmony = new Harmony(HarmonyId);
            int patched = 0;
            foreach (MethodBase target in Targets())
            {
                if (target == null) continue;
                harmony.Patch(target, prefix: prefix, finalizer: finalizer);
                patched++;
            }

            _installed = true;
            Debug.Log($"[KSP-AP/KopernicusCompat] home-body shim installed on "
                    + $"{patched} stock method(s)");
        }

        // The stock methods from which Kopernicus reads FlightGlobals.GetHomeBody()
        // and then requires the result to own a PQSCity named "KSC". A null here
        // means KSP renamed the method; the corresponding Kopernicus patch would
        // not have applied either, so skipping is correct.
        private static IEnumerable<MethodBase> Targets()
        {
            yield return WarnIfMissing(typeof(SpaceCenterCamera2), "Start");
            yield return WarnIfMissing(typeof(PSystemSetup), "SetMainMenu");
            yield return WarnIfMissing(typeof(PSystemSetup), "SetSpaceCentre");
            yield return WarnIfMissing(typeof(PSystemSetup), "SetTrackingStation");
            yield return WarnIfMissing(typeof(PSystemSetup), "SetEditor");
        }

        private static MethodBase WarnIfMissing(Type type, string name)
        {
            MethodBase m = AccessTools.Method(type, name);
            if (m == null)
            {
                Debug.LogWarning($"[KSP-AP/KopernicusCompat] {type.Name}.{name} not "
                               + "found; skipping (KSP version change?)");
            }
            return m;
        }

        // Never allowed to throw: Harmony would still run the finalizer, but the
        // depth counter has to stay balanced for nested calls to restore correctly.
        private static void Prefix()
        {
            try
            {
                if (_depth++ > 0) return;
                CelestialBody ksc = KscBody();
                if (ksc == null || !CacheFields()) return;
                FlightGlobals fg = FlightGlobals.fetch;
                if (fg == null) return;
                _homeBodyField.SetValue(fg, ksc);
                _homeBodyIndexField.SetValue(fg, FlightGlobals.GetBodyIndex(ksc));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[KSP-AP/KopernicusCompat] home-body override failed: "
                               + ex.Message);
            }
        }

        // Clears rather than restores the previous value, matching
        // Materialiser.ClearFlightGlobalsHomeCache: GetHomeBody() re-derives from
        // isHomeWorld on the next call, which is the AP home and is always correct.
        private static void Finalizer()
        {
            try
            {
                if (--_depth > 0) return;
                _depth = 0;
                if (!CacheFields()) return;
                FlightGlobals fg = FlightGlobals.fetch;
                if (fg == null) return;
                _homeBodyField.SetValue(fg, null);
                _homeBodyIndexField.SetValue(fg, 0);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[KSP-AP/KopernicusCompat] home-body restore failed: "
                               + ex.Message);
            }
        }

        /// <summary>
        /// The body whose PQS owns the stock KSC, i.e. the body Kopernicus
        /// expects to be home. Derived from the live PQS hierarchy rather than
        /// hard-coded to "Kerbin" so planet packs that rename or move the home
        /// world resolve correctly. Stock anchors the same lookup on
        /// PSystemSetup.pqsTransformToCache ("KSC"), a direct child of the
        /// body's PQS transform.
        ///
        /// A miss is never cached — the bodies or their PQS may not exist yet
        /// on the first call — so only the warning is suppressed after the
        /// first failure.
        /// </summary>
        private static CelestialBody KscBody()
        {
            if (_kscBody != null) return _kscBody;
            List<CelestialBody> bodies = FlightGlobals.Bodies;
            if (bodies != null)
            {
                for (int i = 0; i < bodies.Count; i++)
                {
                    CelestialBody b = bodies[i];
                    if (b == null || b.pqsController == null) continue;
                    if (b.pqsController.transform.Find("KSC") == null) continue;
                    _kscBody = b;
                    Debug.Log("[KSP-AP/KopernicusCompat] stock KSC resolved to " + b.bodyName);
                    return _kscBody;
                }
            }
            if (!_kscBodyWarned)
            {
                _kscBodyWarned = true;
                Debug.LogWarning("[KSP-AP/KopernicusCompat] no body owns a PQS child named "
                               + "'KSC'; leaving the home-body cache alone");
            }
            return null;
        }

        private static bool CacheFields()
        {
            if (_homeBodyField != null && _homeBodyIndexField != null) return true;
            const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
            _homeBodyField = typeof(FlightGlobals).GetField("homeBody", BF);
            _homeBodyIndexField = typeof(FlightGlobals).GetField("homeBodyIndex", BF);
            if (_homeBodyField != null && _homeBodyIndexField != null) return true;
            Debug.LogWarning("[KSP-AP/KopernicusCompat] FlightGlobals home-cache fields "
                           + $"not found (homeBody={_homeBodyField != null}, "
                           + $"homeBodyIndex={_homeBodyIndexField != null})");
            return false;
        }
    }
}
