using System;
using System.Collections.Generic;
using Archipelago.MultiClient.Net.Enums;
using KSP.UI.Screens;
using UnityEngine;

namespace KSPArchipelago.Traps
{
    /// <summary>
    /// The trap pipeline. Receive side: NoteReceived (called exactly once per
    /// item index, from the awarded-index blocks in ProcessAllItems /
    /// ProcessNewItems) queues known traps into ApScenarioModule.PendingTraps
    /// (save-persisted); only deadly traps announce themselves on receive —
    /// mild ones stay silent until they are about to fire. Fire side: Drain
    /// (called every frame from Update) fires queued traps one at a time.
    /// In flight: a fresh launch gets a randomized 30s-2min mission-time
    /// grace, a vessel switch a short settle, and every fire is preceded by a
    /// 2-3s "something feels off" warning (red-tinted for deadly traps, which
    /// also pace their own fuses); warp is dropped before firing (Time Slip excepted) and
    /// the pipeline freezes entirely while the game is paused. In the Space
    /// Center / Tracking Station scenes, vessel-free traps (Time Slip) fire
    /// directly. FiredTrapStore (persisted outside the save) makes every
    /// fire once-ever: no revert or reconnect brings a suffered trap back.
    /// (Backlog stacking/burst-firing is deliberately deferred until every
    /// actuator exists — the one-at-a-time gate is the thing to relax.)
    /// </summary>
    public static class TrapManager
    {
        private static readonly Dictionary<string, ITrapActuator> Registry =
            new Dictionary<string, ITrapActuator>();

        // Every 0.9.0 trap item name. A name here WITHOUT a Registry entry is
        // a trap whose actuator isn't built yet — it queues and stays pending
        // (never fizzles), so the roster can land actuator-by-actuator.
        private static readonly HashSet<string> KnownTrapNames = new HashSet<string>
        {
            "Trap: Stage Fright",
            "Trap: Gravity Storm",
            "Trap: Spin Cycle",
            "Trap: Radio Silence",
            "Trap: Short Circuit",
            "Trap: Thermal Runaway",
            "Trap: Loose Bolts",
            "Trap: Mandatory Spacewalk",
            "Trap: Time Slip",
            "Trap: Sticky Throttle",
            "Trap: Minor Kraken Attack",
        };

        // Traps scary enough to deserve advance warning: they announce
        // themselves on receive (even out of flight) and get a scarier
        // pre-fire warn toast. Pacing beyond that lives in the actuator
        // (Stage Fright runs its own 15s fuse — a manager-level random
        // dread window play-tested as "looks broken"). Membership is
        // decided per-trap with the operator as actuators are built.
        private static readonly HashSet<string> DeadlyTrapNames = new HashSet<string>
        {
            "Trap: Stage Fright",
        };

        private static TrapEffectsRunner _runner;
        private static uint _armedVesselId = uint.MaxValue;
        private static double _requiredMissionTime;
        private static float _nextFireAt;
        // Warning window: the trap (by item index) whose "something feels
        // off" toast has been posted and which fires at _warnFireAt.
        private static int _warnIndex = -1;
        private static float _warnFireAt;
        // True while a timed effect was running last frame — its end restarts
        // the between-fires cooldown so back-to-back traps don't chain
        // directly off a finished drain/heat effect.
        private static bool _effectWasActive;

        static TrapManager()
        {
            Register(new Actuators.StickyThrottleTrap());
            Register(new Actuators.TimeSlipTrap());
            Register(new Actuators.ShortCircuitTrap());
            Register(new Actuators.SpinCycleTrap());
            Register(new Actuators.GravityStormTrap());
            Register(new Actuators.ThermalRunawayTrap());
            Register(new Actuators.KrakenAttackTrap());
            Register(new Actuators.StageFrightTrap());
            Register(new Actuators.LooseBoltsTrap());
            Register(new Actuators.RadioSilenceTrap());
            Register(new Actuators.MandatorySpacewalkTrap());
        }

        private static void Register(ITrapActuator actuator)
        {
            Registry[actuator.ItemName] = actuator;
            if (!KnownTrapNames.Contains(actuator.ItemName))
                Debug.LogError(
                    $"[KSP-AP] Trap actuator '{actuator.ItemName}' missing from KnownTrapNames");
        }

        public static bool IsTrapItem(string itemName)
            => itemName != null && KnownTrapNames.Contains(itemName);

        /// <summary>
        /// The single enqueue path. Callers guarantee exactly-once per item
        /// index (the !alreadyAwarded blocks) and a live ApScenarioModule.
        /// </summary>
        public static void NoteReceived(int itemIndex, string itemName, ItemFlags flags)
        {
            if (!KnownTrapNames.Contains(itemName))
            {
                // Trap-flagged but unknown: a newer server than this client.
                if ((flags & ItemFlags.Trap) != 0)
                {
                    string msg = $"AP: Unknown trap item '{itemName}' — update the client mod";
                    ScreenMessages.PostScreenMessage(msg, 8f, ScreenMessageStyle.UPPER_CENTER);
                    Debug.LogError($"[KSP-AP] {msg}");
                }
                return;
            }

            if (FiredTrapStore.Contains(itemIndex)) return;   // already suffered — never re-owed

            var scenario = ApScenarioModule.Instance;
            if (scenario == null) return;
            scenario.PendingTraps.Add(new PendingTrap { Index = itemIndex, Name = itemName });

            // Mild traps arrive unannounced; the pre-fire warning in Drain is
            // the player's only tell. Deadly ones get named up front — the
            // dread is the point.
            if (DeadlyTrapNames.Contains(itemName))
                ScreenMessages.PostScreenMessage(
                    $"<color=orange>AP: Received {itemName}.</color> Something feels... off.",
                    6f, ScreenMessageStyle.UPPER_CENTER);
        }

        /// <summary>Called every frame from KSPArchipelagoMod.Update().</summary>
        public static void Drain(KSPArchipelagoMod mod)
        {
            var scenario = ApScenarioModule.Instance;
            if (scenario == null) return;   // EDITOR / menus — no trap state here

            if (!HighLogic.LoadedSceneIsFlight)
            {
                ResetFlightState();
                if (scenario.PendingTraps.Count == 0) return;
                if (!mod.IsConnected || !FiredTrapStore.IsLoaded) return;
                if (GamePaused()) return;
                // Space Center / Tracking Station: both support time warp, so
                // vessel-free traps (Time Slip) fire here instead of waiting
                // for a flight.
                DrainVesselFree(mod, scenario);
                return;
            }

            if (!FlightGlobals.ready || FlightGlobals.ActiveVessel == null)
            {
                ResetFlightState();
                return;
            }
            Vessel v = FlightGlobals.ActiveVessel;

            if (scenario.PendingTraps.Count == 0) return;
            if (!mod.IsConnected || !FiredTrapStore.IsLoaded) return;

            // Never act while paused: TimeWarp.SetRate stomps the pause
            // menu's timeScale=0 and un-pauses the game. Timers keep running
            // in realtime, so owed traps land right after the player resumes.
            if (GamePaused()) return;

            TrapEffectsRunner runner = EnsureRunner(mod);
            if (runner.HasActive)   // one trap at a time
            {
                _effectWasActive = true;
                return;
            }
            if (_effectWasActive)
            {
                // A timed effect just finished — restart the spacing clock so
                // the next trap doesn't land the same instant the last one ends.
                _effectWasActive = false;
                _nextFireAt = Mathf.Max(_nextFireAt,
                    Time.realtimeSinceStartup + UnityEngine.Random.Range(5f, 15f));
                return;
            }

            // Arm once per vessel. A fresh craft (still on the pad or just
            // launched) gets a randomized 30s-2min mission-time grace so the
            // first trap isn't predictable; an established craft (e.g.
            // switched to from the Tracking Station) just gets a short settle.
            if (v.persistentId != _armedVesselId)
            {
                _armedVesselId = v.persistentId;
                _warnIndex = -1;
                _requiredMissionTime =
                    v.missionTime < 30.0 ? UnityEngine.Random.Range(30f, 120f) : 0.0;
                _nextFireAt = Time.realtimeSinceStartup + UnityEngine.Random.Range(1f, 3f);
            }
            if (v.missionTime < _requiredMissionTime) return;

            if (_warnIndex >= 0)
            {
                FireWarned(mod, scenario, v, runner);
                return;
            }

            if (Time.realtimeSinceStartup < _nextFireAt) return;

            // Warping with a warp-intolerant trap owed: drop to 1x now and let
            // the vessel unpack; the pick happens on a later frame.
            if (TimeWarp.CurrentRate > 1f && HasPendingNeedingUnwarp(scenario))
            {
                TimeWarp.SetRate(0, true);
                return;
            }

            // Pick the next fireable trap and open its warning window.
            for (int i = 0; i < scenario.PendingTraps.Count; i++)
            {
                PendingTrap pending = scenario.PendingTraps[i];
                if (FiredTrapStore.Contains(pending.Index))
                {
                    // Stale: this save was written before a fire that already
                    // happened (fire-once-ever wins over save-tied pending).
                    scenario.PendingTraps.RemoveAt(i--);
                    continue;
                }
                if (!Registry.TryGetValue(pending.Name, out ITrapActuator actuator))
                    continue;   // actuator not built yet — stays queued
                if (!actuator.IsEligible(v)) continue;

                _warnIndex = pending.Index;
                bool deadly = DeadlyTrapNames.Contains(pending.Name);
                _warnFireAt = Time.realtimeSinceStartup + UnityEngine.Random.Range(2f, 3f);
                // Deadly traps get a distinct, scarier warn toast; their
                // longer pacing (e.g. Stage Fright's fuse) is the actuator's.
                ScreenMessages.PostScreenMessage(
                    deadly
                        ? "<color=red>AP:</color> Something feels... very wrong."
                        : "<color=orange>AP:</color> Something feels... off.",
                    3f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
        }

        /// <summary>
        /// The warned trap's window elapsed — fire it. The queue entry is
        /// re-located by item index (saves/loads may have shuffled the list);
        /// if it vanished or became ineligible the warning dissolves and the
        /// trap stays pending for a later pick.
        /// </summary>
        private static void FireWarned(
            KSPArchipelagoMod mod, ApScenarioModule scenario, Vessel v, TrapEffectsRunner runner)
        {
            int slot = -1;
            for (int i = 0; i < scenario.PendingTraps.Count; i++)
                if (scenario.PendingTraps[i].Index == _warnIndex)
                {
                    slot = i;
                    break;
                }
            if (slot < 0 || FiredTrapStore.Contains(_warnIndex))
            {
                _warnIndex = -1;
                return;
            }
            if (Time.realtimeSinceStartup < _warnFireAt) return;

            PendingTrap pending = scenario.PendingTraps[slot];
            if (!Registry.TryGetValue(pending.Name, out ITrapActuator actuator))
            {
                _warnIndex = -1;
                return;
            }
            if (!actuator.IsEligible(v))
            {
                // E.g. the player warped during the window: drop warp and wait
                // for the vessel to unpack. Give up after 10s — the trap goes
                // back to plain pending.
                if (!actuator.ManagesWarpItself && TimeWarp.CurrentRate > 1f)
                    TimeWarp.SetRate(0, true);
                if (Time.realtimeSinceStartup > _warnFireAt + 10f) _warnIndex = -1;
                return;
            }

            _warnIndex = -1;
            FireNow(mod, scenario, slot, actuator, v, runner);
        }

        /// <summary>
        /// Fire one pending trap. Removes the entry BEFORE firing so an
        /// actuator exception can't loop-fire, and records the fire either
        /// way — a fizzled trap is consumed, not retried.
        /// </summary>
        private static void FireNow(
            KSPArchipelagoMod mod, ApScenarioModule scenario, int slot,
            ITrapActuator actuator, Vessel v, TrapEffectsRunner runner)
        {
            PendingTrap pending = scenario.PendingTraps[slot];
            scenario.PendingTraps.RemoveAt(slot);
            try
            {
                if (!actuator.ManagesWarpItself && TimeWarp.CurrentRate > 1f)
                    TimeWarp.SetRate(0, true);
                actuator.Fire(mod, v, runner);
                PostTrayRecord(pending.Name);
                Debug.Log($"[KSP-AP] Trap fired: {pending.Name} (item index {pending.Index})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KSP-AP] Trap '{pending.Name}' fizzled: {ex}");
                ScreenMessages.PostScreenMessage(
                    "<color=orange>TRAP:</color> ...something fizzled harmlessly.",
                    4f, ScreenMessageStyle.UPPER_CENTER);
            }
            finally
            {
                FiredTrapStore.Record(pending.Index);
                mod.PushFiredTrap(pending.Index);
                _nextFireAt = Time.realtimeSinceStartup + UnityEngine.Random.Range(5f, 15f);
            }
        }

        /// <summary>
        /// Space Center / Tracking Station drain: fires the first pending
        /// vessel-free trap (no arm/grace/warning — nothing to prep for
        /// without a craft on the line).
        /// </summary>
        private static void DrainVesselFree(KSPArchipelagoMod mod, ApScenarioModule scenario)
        {
            if (Time.realtimeSinceStartup < _nextFireAt) return;
            if (_runner != null && _runner.HasActive) return;

            for (int i = 0; i < scenario.PendingTraps.Count; i++)
            {
                PendingTrap pending = scenario.PendingTraps[i];
                if (FiredTrapStore.Contains(pending.Index))
                {
                    scenario.PendingTraps.RemoveAt(i--);
                    continue;
                }
                if (!Registry.TryGetValue(pending.Name, out ITrapActuator actuator))
                    continue;
                if (!actuator.CanFireWithoutVessel || !actuator.IsEligible(null))
                    continue;

                FireNow(mod, scenario, i, actuator, null, EnsureRunner(mod));
                return;
            }
        }

        /// <summary>Scene teardown: kill live effects; the pending queue is
        /// save-tied state and stays.</summary>
        public static void OnSceneChange()
        {
            _runner?.AbortAll();
            ResetFlightState();
        }

        public static void OnDisconnect()
        {
            _runner?.AbortAll();
            ResetFlightState();
        }

        private static void ResetFlightState()
        {
            _armedVesselId = uint.MaxValue;
            _warnIndex = -1;
            _effectWasActive = false;
        }

        private static bool GamePaused()
            => FlightDriver.Pause || Mathf.Approximately(Time.timeScale, 0f);

        private static bool HasPendingNeedingUnwarp(ApScenarioModule scenario)
        {
            foreach (PendingTrap pending in scenario.PendingTraps)
                if (Registry.TryGetValue(pending.Name, out ITrapActuator actuator)
                    && !actuator.ManagesWarpItself
                    && !FiredTrapStore.Contains(pending.Index))
                    return true;
            return false;
        }

        private static TrapEffectsRunner EnsureRunner(KSPArchipelagoMod mod)
        {
            if (_runner == null)
                _runner = mod.gameObject.AddComponent<TrapEffectsRunner>();
            return _runner;
        }

        private static void PostTrayRecord(string trapName)
        {
            if (MessageSystem.Instance == null) return;
            MessageSystem.Instance.AddMessage(new MessageSystem.Message(
                trapName,
                $"{trapName} struck your vessel, courtesy of the multiworld.",
                MessageSystemButton.MessageButtonColor.ORANGE,
                MessageSystemButton.ButtonIcons.FAIL));
        }
    }
}
