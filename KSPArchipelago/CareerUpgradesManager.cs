using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Upgradeables;

namespace KSPArchipelago
{
    /// <summary>
    /// Career mode facility upgrade gate. Validated mechanism from the spike:
    ///
    ///   1. Button intercept (primary): every frame in SpaceCentre, scan for
    ///      UnityEngine.UI.Button at the exact path
    ///      `_UIMaster/MainCanvas/&lt;Facility&gt; Context Menu/UpgradeButton`,
    ///      replace onClick with our handler. Block is clean: no upgrade
    ///      animation, no funds deducted, no SetLevel call. The click itself
    ///      is "the check" — see Phase 2.3 of plans/career_mode_migration.md.
    ///
    ///   2. POST-revert (fallback): on GameEvents.OnKSCFacilityUpgraded, if
    ///      `newLvl &gt; apGrantedLevel[facilityId]`, immediately call
    ///      SetLevel(apGrantedLevel) to revert. Catches edge cases the
    ///      button intercept missed. Recursion guard via `newLvl &gt; allowed`
    ///      check — our self-reverts land at exactly `allowed`, no loop.
    ///
    ///   3. Apply: when AP grants a `Progressive &lt;Facility&gt;` item,
    ///      `ApplyApGrantedLevel(facilityId, level)` increments the tracker
    ///      and calls UpgradeableFacility.SetLevel(level) on the live
    ///      instance — which also fires OnKSCFacilityUpgraded so the
    ///      observer side sees the change naturally.
    ///
    /// 9 facilities (FlagPole and Observatory excluded — not UpgradeableFacility
    /// MonoBehaviours per probe finding H1.2):
    ///   VAB, SPH, LaunchPad, Runway, TrackingStation, MissionControl,
    ///   AstronautComplex, ResearchAndDevelopment, Administration.
    ///
    /// Initial apGrantedLevel defaults are 0 (KSP stock career start).
    /// Hardcoded test values during career-branch wiring; production reads
    /// from ApScenarioModule persisted state once that field is added.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class CareerUpgradesManager : MonoBehaviour
    {
        public static CareerUpgradesManager Instance { get; private set; }

        // KSP facility id strings (probe-confirmed: `SpaceCenter/<X>` format,
        // 9 in-scene UpgradeableFacility instances).
        public static readonly string[] FacilityIds = new[]
        {
            "SpaceCenter/VehicleAssemblyBuilding",
            "SpaceCenter/SpaceplaneHangar",
            "SpaceCenter/LaunchPad",
            "SpaceCenter/Runway",
            "SpaceCenter/TrackingStation",
            "SpaceCenter/MissionControl",
            "SpaceCenter/AstronautComplex",
            "SpaceCenter/ResearchAndDevelopment",
            "SpaceCenter/Administration",
        };

        // Server-provided item→facility map (from slot_data
        // career.facility_item_map), pushed by CareerHackManager at connect.
        // Replaces the old hardcoded name map (whose keys had drifted from the
        // generator item names, e.g. "Progressive RnD" vs "Progressive R&D") — the
        // client now actuates whatever facilities the generator sends, so adding a
        // building is a server-only change. Each item's facility level is derived
        // from the count of that item received via its thresholds.
        private Dictionary<string, FacilityItemSpec> _facilityItemMap
            = new Dictionary<string, FacilityItemSpec>(StringComparer.Ordinal);

        public Dictionary<string, FacilityItemSpec> FacilityItemMap => _facilityItemMap;

        public void SetFacilityItemMap(Dictionary<string, FacilityItemSpec> map)
        {
            _facilityItemMap = map
                ?? new Dictionary<string, FacilityItemSpec>(StringComparer.Ordinal);
        }

        // Facility level for a received-item count: the number of thresholds the
        // count has reached. thresholds [1,2] → increment; [2,4] → the R&D
        // facility's non-linear (samples at 2, top at 4) schedule.
        public static int LevelForCount(List<int> thresholds, int count)
        {
            if (thresholds == null) return 0;
            int level = 0;
            foreach (int t in thresholds) if (count >= t) level++;
            return level;
        }

        // Maximum AP-granted level per facility. Stock has 3 levels (0/1/2);
        // value here is the MAX (inclusive) so `Math.Min(level, max)` clamps.
        public const int MaxLevel = 2;

        // Per-facility AP-granted level (baseline + progressive increments).
        // Defaults to 0 (stock career start). Persistence is delegated to
        // ApScenarioModule (see SetApGrantedLevel / SnapshotBaselineLevels).
        private readonly Dictionary<string, int> _apGrantedLevel
            = new Dictionary<string, int>();

        // Career-directive (and save-restored) facility baseline — the floor that
        // AP grants independently of "Progressive <Facility>" items. _apGrantedLevel
        // = baseline + progressive increments. ResetApGrantedLevels resets to THIS
        // (not 0) so a connect-time reset can't wipe the directive baseline before
        // the async OnKSCFacilityUpgraded events arrive (the bug that clamped the
        // materialiser-cycled Astronaut Complex to 0 via POST-revert with allowed=0).
        private readonly Dictionary<string, int> _baselineLevel
            = new Dictionary<string, int>();

        // True once CareerHackManager has pushed the real per-facility levels
        // from the `career` slot_data block, i.e. the AP-granted cap is now
        // authoritative. Until then _apGrantedLevel holds the all-zero default,
        // which must NOT be used to revert facilities (see OnFacilityUpgraded):
        // at connect the alien-KSC materialiser brings facilities up to their
        // saved level and fires OnKSCFacilityUpgraded while this is still the
        // default 0, and an ungated revert would clamp every building to 0
        // until the next Space Center entry re-applied the stored levels.
        private bool _baselineApplied = false;

        // Called by CareerHackManager after it applies the career directives'
        // building_levels, marking the AP-granted cap authoritative so the
        // POST-revert may begin enforcing it.
        public void MarkBaselineApplied() => _baselineApplied = true;

        // Hijacked button instances, identified by Unity instance id so we
        // don't re-hijack the same Button across multiple scans.
        private readonly HashSet<int> _hijackedIds = new HashSet<int>();
        private readonly List<Button> _hijackedButtons = new List<Button>();

        /// <summary>
        /// Per-facility "expected stable level". When set, OnKSCFacilityUpgraded
        /// POST-revert is suppressed for that facility until the event reports
        /// newLvl == expectedStableLevel — i.e. the facility has settled at
        /// its intended post-transition state. Used by the KSC materialiser:
        /// it forces AstronautComplex to L3, waits, restores to origLevel, and
        /// publishes origLevel as the expectation. POST-revert ignores the
        /// intermediate L3 event AND the restore event, then resumes normal
        /// behaviour. Robust against KSP's SetLevel coroutine racing past the
        /// materialiser's finally block.
        /// </summary>
        private readonly Dictionary<string, int> _expectedStableLevel
            = new Dictionary<string, int>();

        /// <summary>
        /// Begin suppressing POST-revert for a facility. Suppression auto-clears
        /// the next time OnKSCFacilityUpgraded fires for that facility with
        /// newLvl == expectedStableLevel. Use to wrap temporary SetLevel calls
        /// from the KSC materialiser (force-AC-to-L3 trick).
        /// </summary>
        public void ExpectStableLevel(string facilityId, int level)
        {
            if (string.IsNullOrEmpty(facilityId)) return;
            _expectedStableLevel[facilityId] = level;
        }

        /// <summary>
        /// Force-clear any expected-stable-level entry for a facility. Use
        /// only when the materialiser is certain the level state is now
        /// stable (e.g. on error/abort paths where the event may never fire).
        /// </summary>
        public void ClearExpectedStableLevel(string facilityId)
        {
            if (string.IsNullOrEmpty(facilityId)) return;
            _expectedStableLevel.Remove(facilityId);
        }

        void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(this);

            foreach (string id in FacilityIds) { _apGrantedLevel[id] = 0; _baselineLevel[id] = 0; }

            try { GameEvents.OnKSCFacilityUpgraded.Add(OnFacilityUpgraded); }
            catch (Exception ex)
            {
                Debug.LogError($"[KSP-AP] OnKSCFacilityUpgraded subscribe failed: {ex}");
            }

            // onGameSceneLoadRequested fires *before* the new scene is up
            // — used only for invalidating per-scene UI references that we
            // own. The post-load resync runs from onLevelWasLoadedGUIReady
            // (below).
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelLoadedGUIReady);
        }

        void OnDestroy()
        {
            try { GameEvents.OnKSCFacilityUpgraded.Remove(OnFacilityUpgraded); } catch { }
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelLoadedGUIReady);
            if (Instance == this) Instance = null;
        }

        private void OnSceneChange(GameScenes scene)
        {
            // KSP rebuilds the KSC UI on every SpaceCentre entry — the
            // button instances are stale. Clear the hijacked-ids set so
            // we re-hijack on the new instances. (This fires pre-load,
            // so it's fine to clear here.)
            _hijackedIds.Clear();
            _hijackedButtons.Clear();
        }

        private void OnLevelLoadedGUIReady(GameScenes scene)
        {
            // Re-sync any AP-granted levels that didn't apply earlier.
            // Items received outside SpaceCenter (flight, editor) update
            // the tracker but can't SetLevel because UpgradeableFacility
            // instances aren't findable there. Apply now that the scene
            // is fully up. Upgrade-only (never downgrade) — the player's
            // save baseline is preserved.
            if (scene == GameScenes.SPACECENTER)
            {
                StartCoroutine(ReapplyApGrantedLevelsNextFrame());
            }
        }

        private IEnumerator ReapplyApGrantedLevelsNextFrame()
        {
            yield return null; // wait one frame
            yield return null; // and another — facility GameObjects need a beat to register
            foreach (var kv in _apGrantedLevel)
            {
                string facilityId = kv.Key;
                int storedLevel = kv.Value;
                UpgradeableFacility f = FindFacilityById(facilityId);
                if (f == null) continue;
                int liveLevel;
                try { liveLevel = f.FacilityLevel; } catch { continue; }
                // Upgrade-only re-sync. Never call SetLevel to downgrade a
                // facility: the player's save may start with facilities at
                // a higher baseline than apGrantedLevel (e.g. "fully upgraded
                // buildings" Career setting), and we don't want to clobber
                // that. POST-revert in OnFacilityUpgraded still catches the
                // case where someone went *past* what AP granted.
                if (storedLevel <= liveLevel) continue;
                Debug.Log($"[KSP-AP] Re-applying AP-granted upgrade on '{facilityId}': "
                        + $"liveLevel={liveLevel} -> storedLevel={storedLevel}");
                // Mark our own SetLevel as expected so its async upgrade event
                // isn't reverted by POST-revert (see ApplyLevelToLiveFacility).
                _expectedStableLevel[facilityId] = storedLevel;
                try { f.SetLevel(storedLevel); }
                catch (Exception ex)
                {
                    Debug.LogError($"[KSP-AP] re-apply SetLevel({storedLevel}) on "
                                 + $"'{facilityId}' threw: {ex}");
                }
            }
        }

        void Update()
        {
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER) return;
            if (HighLogic.CurrentGame == null
                || HighLogic.CurrentGame.Mode != Game.Modes.CAREER) return;
            ScanAndHijackUpgradeButtons();
        }

        private void ScanAndHijackUpgradeButtons()
        {
            // Probe finding: every facility's upgrade button lives at
            // `_UIMaster/MainCanvas/<Facility> Context Menu/UpgradeButton`.
            // The button GameObject's name is consistently "UpgradeButton";
            // we filter on that AND verify the parent path ends in
            // "... Context Menu" to avoid grabbing unrelated upgrade
            // buttons (e.g. KK launch-site editor UI).
            foreach (Button b in GameObject.FindObjectsOfType<Button>())
            {
                if (b == null) continue;
                if (b.name != "UpgradeButton") continue;
                int id = b.GetInstanceID();
                if (_hijackedIds.Contains(id)) continue;

                Transform parent = b.transform.parent;
                if (parent == null) continue;
                string parentName = parent.name;
                if (!parentName.EndsWith(" Context Menu")) continue;

                string facilityShortName = parentName.Substring(0,
                    parentName.Length - " Context Menu".Length);

                Button captured = b;
                captured.onClick = new Button.ButtonClickedEvent();
                captured.onClick.AddListener(() => HandleUpgradeClick(facilityShortName, captured));
                _hijackedButtons.Add(captured);
                _hijackedIds.Add(id);
            }
        }

        private void HandleUpgradeClick(string facilityShortName, Button button)
        {
            // Resolve facility id from short name.
            string facilityId = ResolveFacilityIdByShortName(facilityShortName);
            int currentLevel = facilityId != null
                ? GetCurrentLevel(facilityId)
                : -1;
            int targetLevel = currentLevel + 1;

            string apCheckName = $"Try Upgrade: {facilityShortName} Lv{targetLevel + 1}";
            Debug.Log($"[KSP-AP] Career upgrade intercept: "
                    + $"facility='{facilityShortName}' id='{facilityId}' "
                    + $"currentLevel={currentLevel} targetLevel={targetLevel} "
                    + $"check='{apCheckName}'");

            // Fire the AP location check. MissionTracker.ReportLocation
            // dedups by name + handles offline queueing. Safe to call on
            // every click — repeated clicks on the same level become no-ops
            // after the first.
            var mod = UnityEngine.Object.FindObjectOfType<KSPArchipelagoMod>();
            var tracker = mod?.Tracker;
            tracker?.ReportLocation(apCheckName);

            // The Progressive item that unlocks this facility — reverse-lookup of
            // the server's facility_item_map. Null for an ungated facility.
            string progressiveItem = null;
            foreach (var kv in _facilityItemMap)
            {
                if (kv.Value?.Facilities != null && kv.Value.Facilities.Contains(facilityId))
                {
                    progressiveItem = kv.Key;
                    break;
                }
            }

            // Facility upgrades are gated by the multiworld: the building rises when
            // the matching Progressive item arrives, not from clicking — so point the
            // player at that item. (The knownOnServer branch stays for any seed that
            // models the upgrade as an explicit "Try Upgrade" check.)
            bool knownOnServer = tracker != null && tracker.IsLocationKnown(apCheckName);
            string restricted = progressiveItem != null
                ? $"<color=yellow>Building upgrades are restricted — look for the item \"{progressiveItem}\".</color>"
                : "<color=yellow>Building upgrades are restricted by the multiworld.</color>";
            string toast = knownOnServer
                ? $"<color=#7FFF7F>[AP] Upgrade check logged:</color> {apCheckName}\n"
                  + "<color=#A0E0FF>Building stays locked until the matching Progressive item arrives from the multiworld.</color>"
                : $"<color=orange>[AP] {facilityShortName} upgrade</color>\n" + restricted;
            ScreenMessages.PostScreenMessage(toast, 5f, ScreenMessageStyle.UPPER_CENTER);
        }

        private static string ResolveFacilityIdByShortName(string shortName)
        {
            // KSC context-menu parents use short names like "VAB", "Astronaut Complex",
            // "Tracking Station", etc. Map to canonical SpaceCenter/<X> ids by
            // matching against the trailing suffix.
            switch (shortName)
            {
                case "VAB":                 return "SpaceCenter/VehicleAssemblyBuilding";
                case "SPH":                 return "SpaceCenter/SpaceplaneHangar";
                case "LaunchPad":           return "SpaceCenter/LaunchPad";
                case "Runway":              return "SpaceCenter/Runway";
                case "TrackingStation":     return "SpaceCenter/TrackingStation";
                case "MissionControl":      return "SpaceCenter/MissionControl";
                case "AstronautComplex":    return "SpaceCenter/AstronautComplex";
                case "RnD":
                case "ResearchAndDevelopment":
                                            return "SpaceCenter/ResearchAndDevelopment";
                case "Administration":      return "SpaceCenter/Administration";
                default:                    return null;
            }
        }

        private void OnFacilityUpgraded(UpgradeableFacility facility, int newLvl)
        {
            if (facility == null) return;
            string facilityId = SafeGetId(facility);
            if (string.IsNullOrEmpty(facilityId)) return;

            // Diagnostic: shows why this event reverts/skips. Fires only on
            // facility upgrades (not per-frame). Expect AC to read allowed=2 now.
            Debug.Log($"[KSP-AP] OnFacilityUpgraded: id='{facilityId}' newLvl={newLvl} "
                    + $"baselineApplied={_baselineApplied} "
                    + $"baseline={(_baselineLevel.TryGetValue(facilityId, out int dbg_b) ? dbg_b : -1)} "
                    + $"allowed={(_apGrantedLevel.TryGetValue(facilityId, out int dbg_a) ? dbg_a : -1)} "
                    + $"expected={(_expectedStableLevel.TryGetValue(facilityId, out int dbg_e) ? dbg_e : -1)}");

            // Per-facility suppression for the materialiser's force-AC-to-L3
            // trick. Skip POST-revert while we're awaiting the stable level;
            // once we see the expected newLvl, clear the entry and exit
            // without reverting (the level is by-design at this point).
            if (_expectedStableLevel.TryGetValue(facilityId, out int expected))
            {
                if (newLvl == expected)
                {
                    _expectedStableLevel.Remove(facilityId);
                }
                return;
            }

            // Don't enforce the AP-granted cap until the career directives have
            // established the real baseline. Before that, _apGrantedLevel is the
            // all-zero default; reverting against it at connect clamps the
            // materialiser's level-2 facilities to 0 (the recover-on-next-SC-entry
            // bug). Once CareerHackManager.ApplyBuildingLevels has run, the cap
            // is authoritative and normal POST-revert protection resumes.
            if (!_baselineApplied) return;

            int allowed;
            if (!_apGrantedLevel.TryGetValue(facilityId, out allowed))
            {
                // Unknown facility — leave alone.
                return;
            }
            // POST-revert: only revert if KSP went past what AP granted.
            // Our self-reverts land at exactly `allowed`, so condition is
            // false and recursion stops naturally.
            if (newLvl > allowed)
            {
                Debug.Log($"[KSP-AP] POST-revert: facility='{facilityId}' "
                        + $"newLvl={newLvl} > allowed={allowed} -> SetLevel({allowed})");
                try { facility.SetLevel(allowed); }
                catch (Exception ex)
                {
                    Debug.LogError($"[KSP-AP] POST-revert SetLevel threw: {ex}");
                }
            }
        }

        /// <summary>
        /// Increment the AP-granted level for a facility by one (clamped
        /// to MaxLevel) and call SetLevel on the live UpgradeableFacility
        /// instance. Called from KSPArchipelagoPartsManager.GiveItem when
        /// a "Progressive &lt;Facility&gt;" item arrives.
        /// </summary>
        public int IncrementApGrantedLevel(string facilityId)
        {
            if (string.IsNullOrEmpty(facilityId)) return 0;
            int current = _apGrantedLevel.TryGetValue(facilityId, out int v) ? v : 0;
            int next = Math.Min(current + 1, MaxLevel);
            _apGrantedLevel[facilityId] = next;
            ApplyLevelToLiveFacility(facilityId, next);
            return next;
        }

        /// <summary>
        /// Raise the AP-granted level for a facility to a level computed from a
        /// received-item count (see <see cref="LevelForCount"/>), floored at the
        /// directive baseline and clamped to MaxLevel, then applied to the live
        /// instance (upgrade-only). Unlike <see cref="IncrementApGrantedLevel"/>
        /// this is absolute-from-count, so re-walking items on rebuild is
        /// idempotent, and a latent (ungated, baseline == MaxLevel) facility such
        /// as R&amp;D stays maxed and is never lowered.
        /// </summary>
        public int RaiseApGrantedLevelTo(string facilityId, int level)
        {
            if (string.IsNullOrEmpty(facilityId)) return 0;
            int baseline = _baselineLevel.TryGetValue(facilityId, out int b) ? b : 0;
            int target = Math.Min(Math.Max(level, baseline), MaxLevel);
            _apGrantedLevel[facilityId] = target;
            ApplyLevelToLiveFacility(facilityId, target);
            return target;
        }

        /// <summary>
        /// Set the AP-granted level for a facility to a specific value
        /// (clamped to [0, MaxLevel]) and apply to the live instance.
        /// Used during slot_data load / save restore.
        /// </summary>
        public void SetApGrantedLevel(string facilityId, int level)
        {
            if (string.IsNullOrEmpty(facilityId)) return;
            int clamped = Math.Min(Math.Max(level, 0), MaxLevel);
            // This is an absolute AP-granted level (career directive / save
            // restore), i.e. the baseline. Progressive items increment above it.
            _baselineLevel[facilityId] = clamped;
            _apGrantedLevel[facilityId] = clamped;
            ApplyLevelToLiveFacility(facilityId, clamped);
        }

        public int GetApGrantedLevel(string facilityId)
            => _apGrantedLevel.TryGetValue(facilityId, out int v) ? v : 0;

        // Snapshot the BASELINE for persistence (not _apGrantedLevel, which also
        // includes progressive-item increments). On load, ProcessAllItems re-walks
        // the items and re-adds those increments on top of the restored baseline —
        // persisting the combined value would double-count them.
        public Dictionary<string, int> SnapshotBaselineLevels()
            => new Dictionary<string, int>(_baselineLevel);

        /// <summary>
        /// Zero out all AP-granted levels in memory. Counterpart to
        /// KSPArchipelagoMod.ResetProgressiveState — the mod rebuilds
        /// progressive state from AllItemsReceived on _needsReset
        /// (fires on onGameStateLoad, i.e. VAB entry/exit). Without
        /// this counterpart, every VAB transition would re-Increment
        /// _apGrantedLevel and SetLevel would push the facility one
        /// level higher each time the player visits the editor.
        ///
        /// Does NOT call SetLevel — live UpgradeableFacility instances
        /// stay at their current level. ProcessAllItems re-walking
        /// items will call ApplyLevelToLiveFacility for each Progressive
        /// item; the upgrade-only guard there (level &lt;= liveLevel)
        /// keeps things idempotent.
        ///
        /// Resets to the directive/save BASELINE, not 0: the baseline is the
        /// AP-granted floor that ProcessAllItems does NOT re-apply (it only
        /// re-walks Progressive items). Resetting to 0 wiped the baseline at
        /// connect, leaving POST-revert with allowed=0 — which clamped the
        /// Astronaut Complex to 0 once the materialiser consumed its guard.
        /// </summary>
        public void ResetApGrantedLevels()
        {
            foreach (string id in FacilityIds)
                _apGrantedLevel[id] = _baselineLevel.TryGetValue(id, out int b) ? b : 0;
        }

        private void ApplyLevelToLiveFacility(string facilityId, int level)
        {
            UpgradeableFacility facility = FindFacilityById(facilityId);
            if (facility == null)
            {
                // Scene not loaded yet — the OnLevelLoadedGUIReady re-sync
                // coroutine will reapply once the scene's facility GameObjects
                // are spawned.
                Debug.Log($"[KSP-AP] facility '{facilityId}' not found in scene "
                        + $"(level={level} stored, will apply on next SC entry)");
                return;
            }
            int liveLevel;
            try { liveLevel = facility.FacilityLevel; }
            catch (Exception ex)
            {
                Debug.LogError($"[KSP-AP] FacilityLevel read failed for "
                             + $"'{facilityId}': {ex}");
                return;
            }
            // Upgrade-only: never downgrade a live facility. The player's
            // save may start with facilities above the AP-granted level
            // (e.g. "fully upgraded buildings" Career setting, or normal
            // upgrades they got from earlier saves), and we don't clobber
            // that. apGrantedLevel is a floor.
            if (level <= liveLevel)
            {
                Debug.Log($"[KSP-AP] ApplyLevel skipped for '{facilityId}': "
                        + $"requested={level} <= live={liveLevel} (no-op)");
                return;
            }
            try
            {
                // KSP fires OnKSCFacilityUpgraded ASYNCHRONOUSLY (a frame or two
                // later). By then _apGrantedLevel may have been zeroed by a
                // ResetApGrantedLevels (the per-rebuild reset), so the POST-revert
                // would see newLvl > allowed=0 and clamp our just-applied level
                // straight back to 0. Mark this as an expected, AP-driven level
                // so the revert ignores OUR OWN upgrade event (the same mechanism
                // the KSC materialiser uses for its temporary SetLevels).
                _expectedStableLevel[facilityId] = level;
                facility.SetLevel(level);
                Debug.Log($"[KSP-AP] SetLevel({level}) applied to '{facilityId}' "
                        + $"(was live={liveLevel})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KSP-AP] SetLevel({level}) on '{facilityId}' threw: {ex}");
            }
        }

        private static UpgradeableFacility FindFacilityById(string facilityId)
        {
            foreach (UpgradeableFacility f in
                     GameObject.FindObjectsOfType<UpgradeableFacility>())
            {
                if (SafeGetId(f) == facilityId) return f;
            }
            return null;
        }

        private static string SafeGetId(UpgradeableFacility f)
        {
            // UpgradeableObject.id is a public string field (probe-confirmed).
            try
            {
                var field = typeof(UpgradeableObject).GetField("id",
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance);
                return field?.GetValue(f) as string;
            }
            catch { return null; }
        }

        private static int GetCurrentLevel(string facilityId)
        {
            UpgradeableFacility f = FindFacilityById(facilityId);
            if (f == null) return -1;
            try { return f.FacilityLevel; } catch { return -1; }
        }
    }
}
