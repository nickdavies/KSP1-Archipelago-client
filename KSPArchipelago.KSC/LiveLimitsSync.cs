using System;
using UnityEngine;
using Upgradeables;

namespace KSPArchipelago.KSC
{
    /// <summary>
    /// On every facility upgrade event (user-clicked OR programmatic via
    /// CareerUpgradesManager.SetLevel), update the matching alien KK
    /// launch site's MaxCraftMass so KK's native check stays in sync
    /// with the AP-granted facility level.
    ///
    /// Kerbin starts: no alien sites exist; UpdateAlienPadMaxCraftMass
    /// is a no-op. Stock Kerbin LaunchPad enforcement happens via the
    /// editor warning path (which reads GameVariables.GetCraftMassLimit
    /// and therefore honours APCareerGameVariables).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class LiveLimitsSync : MonoBehaviour
    {
        void Awake()
        {
            DontDestroyOnLoad(this);
            try { GameEvents.OnKSCFacilityUpgraded.Add(OnFacilityUpgraded); }
            catch (Exception ex)
            {
                Debug.LogError("[KSPArchipelago.KSC] LiveLimitsSync subscribe failed: " + ex);
            }
            // Spawn-side scene event fires AFTER scene-loaded UI is up — good
            // place to drain any deferred model swaps that came in while we
            // were on Flight / TrackingStation / etc.
            GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelLoaded);
        }

        void OnDestroy()
        {
            try { GameEvents.OnKSCFacilityUpgraded.Remove(OnFacilityUpgraded); } catch { }
            try { GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelLoaded); } catch { }
        }

        private void OnLevelLoaded(GameScenes scene)
        {
            if (scene == GameScenes.SPACECENTER)
            {
                try { Materialiser.DrainPendingFacilitySwaps(); }
                catch (Exception ex)
                {
                    Debug.LogError("[KSPArchipelago.KSC] DrainPendingFacilitySwaps failed: " + ex);
                }

                // KSP / KK reset stock Kerbin LaunchPad/Runway editorFacility
                // back to VAB/SPH on scene transitions and on Career save
                // load (KK CareerState.FixKSCFacilities, observed
                // 2026-05-30: 10s gap between SC entry and AP connect where
                // stock click-launch was live). Three cases:
                //   - ApHomeConfirmed=false: AP hasn't told us the starting
                //     body yet. Hide preemptively so the click-launch path
                //     is dead. Preserves MH sites (doesn't clear
                //     PSystemSetup.LaunchSites) so a Kerbin-start player
                //     still has them once confirm fires.
                //   - ApHomeConfirmed=true + CurrentBody != Kerbin: alien
                //     start. Full HideKerbinLaunchSites (clears MH sites).
                //   - ApHomeConfirmed=true + CurrentBody == Kerbin: no-op.
                //     SelectorScenarioModule already called
                //     RestoreStockKerbinLaunchSites.
                try
                {
                    if (!Materialiser.ApHomeConfirmed)
                    {
                        Materialiser.PreflightHideStockKerbinPads();
                        Debug.Log("[KSPArchipelago.KSC] SC entry: stock Kerbin "
                                + "pads hidden preemptively (AP home not confirmed)");
                    }
                    else if (Materialiser.CurrentBody != null
                             && Materialiser.CurrentBody != "Kerbin")
                    {
                        Materialiser.HideKerbinLaunchSites();
                        Debug.Log("[KSPArchipelago.KSC] Re-applied HideKerbinLaunchSites on SC entry");
                    }
                    // Re-apply defaultVABLaunchSite / defaultSPHLaunchSite
                    // each SC entry so click-on-stock-Kerbin-LaunchPad routes
                    // to the alien pad. KK CareerState / EditorDriver may
                    // reset these between scenes; re-applying is idempotent.
                    if (Materialiser.ApHomeConfirmed
                        && !string.IsNullOrEmpty(Materialiser.CurrentBody))
                    {
                        Materialiser.SetDefaultLaunchSitesForBody(Materialiser.CurrentBody);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("[KSPArchipelago.KSC] SC-entry hide-pads failed: " + ex);
                }
            }
        }

        private void OnFacilityUpgraded(UpgradeableFacility facility, int newLvl)
        {
            if (facility == null) return;
            string id = SafeGetId(facility);
            if (string.IsNullOrEmpty(id)) return;

            // LaunchPad / Runway: update KKLaunchSite.MaxCraftMass so KK's
            // native check tracks the AP-granted facility level.
            if (id == "SpaceCenter/LaunchPad" || id == "SpaceCenter/Runway")
            {
                try { Materialiser.UpdateAlienPadMaxCraftMass(id); }
                catch (Exception ex)
                {
                    Debug.LogError($"[KSPArchipelago.KSC] LiveLimitsSync mass update "
                                 + $"failed for '{id}': {ex}");
                }
            }

            // All 9 facilities: swap the alien-KSC StaticInstance model so
            // the visual matches the AP-granted level. KK has KSC_<X>_level_1/2/3
            // models for every facility (probe-confirmed).
            try { Materialiser.UpdateAlienFacilityModel(id); }
            catch (Exception ex)
            {
                Debug.LogError($"[KSPArchipelago.KSC] LiveLimitsSync model swap "
                             + $"failed for '{id}': {ex}");
            }

            // CRITICAL: SetLevel internally re-derives
            // PSystemSetup.SpaceCenterFacilities[].editorFacility for the
            // affected facility, restoring it to VAB/SPH default and undoing
            // HideKerbinLaunchSites. On alien starts that re-enables the
            // visible Kerbin LaunchPad's left-click "Enter" handler,
            // letting the player launch from Kerbin instead of the alien
            // KSC. Re-apply the hide whenever any facility upgrades — it's
            // idempotent and cheap, and covers the re-sync coroutine, AP
            // item grants, and any future SetLevel callers.
            try
            {
                if (Materialiser.CurrentBody != null
                    && Materialiser.CurrentBody != "Kerbin")
                {
                    Materialiser.HideKerbinLaunchSites();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KSPArchipelago.KSC] HideKerbinLaunchSites "
                             + $"re-apply (after SetLevel for '{id}') failed: {ex}");
            }

            // Re-pin the default VAB/SPH launch site to the alien pad. KSP's
            // EditorDriver / EditorLaunchGate paths can rewrite
            // HighLogic.CurrentGame.defaultVABLaunchSite as a side effect of
            // facility upgrades and editor transitions. If we let it slip
            // back to "LaunchPad" while the player is mid-session on an
            // alien start, the next click on the visible Kerbin pad routes
            // to Kerbin again (see LaunchSiteFacility.showShipSelection
            // decompile). Idempotent.
            try
            {
                if (Materialiser.ApHomeConfirmed
                    && !string.IsNullOrEmpty(Materialiser.CurrentBody))
                {
                    Materialiser.SetDefaultLaunchSitesForBody(Materialiser.CurrentBody);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KSPArchipelago.KSC] SetDefaultLaunchSitesForBody "
                             + $"re-apply (after SetLevel for '{id}') failed: {ex}");
            }
        }

        private static string SafeGetId(UpgradeableFacility f)
        {
            try
            {
                var field = typeof(UpgradeableObject).GetField("id",
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance);
                return field?.GetValue(f) as string;
            }
            catch { return null; }
        }
    }
}
