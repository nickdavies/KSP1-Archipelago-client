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
    /// mild ones stay silent until they are about to fire.
    ///
    /// Fire side: Drain (called every frame from Update) drains the queue
    /// CONTINUOUSLY — one trap every FireStagger seconds, in arrival order,
    /// skipping (never blocking on) traps that are momentarily ineligible.
    /// Effects overlap freely: a trap landing ten seconds into a live thirty
    /// second Radio Silence applies immediately. Traps that conflict or cancel
    /// each other out are allowed to; that is the point. The two same-type
    /// pairs that would corrupt state or stop being fun instead resolve inside
    /// their own actuator at fire time (Radio Silence coalesces, Stage Fright
    /// swallows the extra copy).
    ///
    /// The "something feels off" warning marks the END of a lull, not each
    /// trap: it is posted only when there has been no trap activity for
    /// LullSeconds, or after a vessel change / scene re-entry. Within a wave
    /// traps just land. In flight a fresh launch also gets a randomized
    /// 30s-2min mission-time grace and a vessel switch a short settle; warp is
    /// dropped before firing (Time Slip excepted) and the pipeline freezes
    /// entirely while the game is paused. In the Space Center / Tracking
    /// Station scenes, vessel-free traps (Time Slip) drain directly.
    ///
    /// FiredTrapStore (persisted outside the save) makes every fire once-ever:
    /// no revert or reconnect brings a suffered trap back. Trap identity is
    /// the received item index and it never outlives a frame here — each fire
    /// resolves its queue slot in the same frame it consumes it.
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

        // Seconds between consecutive fires. The only spacing there is: traps
        // are meant to arrive as a wave, not a drip.
        private const float FireStagger = 0.5f;
        // No trap activity for this long re-arms the "something feels off"
        // warning. Inside a wave the player gets no further warnings.
        private const float LullSeconds = 10f;

        private static TrapEffectsRunner _runner;
        private static uint _armedVesselId = uint.MaxValue;
        private static double _requiredMissionTime;
        private static float _nextFireAt;
        // Realtime of the last trap activity: any fire, and every frame an
        // effect is live. LullSeconds of quiet re-arms the warning.
        private static float _lastTrapActivityAt = float.NegativeInfinity;
        // Situations that earn a fresh warning outright, however recently a
        // trap fired: a new vessel, or leaving and re-entering flight. Cleared
        // by the warning it triggers.
        private static bool _forceWarn = true;
        // Realtime of the last warp drop made on a trap's behalf. See the
        // unwarp block in Drain: dropping warp is a bet, and this is when the
        // bet was placed.
        private static float _unwarpAt = float.NegativeInfinity;
        // How long a warp drop has to produce a fire before we conclude the
        // owed traps simply cannot fire on this craft.
        private const float UnwarpGrace = 5f;

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

            // Before any early-out: a live effect is trap activity even with an
            // empty queue, and that is what keeps the lull clock from expiring
            // during a long trap (a 90s Radio Silence must not be followed by a
            // warning for a trap that lands seconds after it clears).
            if (_runner != null && _runner.HasActive)
                _lastTrapActivityAt = Time.realtimeSinceStartup;

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

            // Arm once per vessel. A fresh craft (still on the pad or just
            // launched) gets a randomized 30s-2min mission-time grace so the
            // first trap isn't predictable; an established craft (e.g.
            // switched to from the Tracking Station) just gets a short settle.
            // A new craft always warns, however recently traps were firing.
            if (v.persistentId != _armedVesselId)
            {
                _armedVesselId = v.persistentId;
                _forceWarn = true;
                _unwarpAt = float.NegativeInfinity;   // new craft, new eligibility
                _requiredMissionTime =
                    v.missionTime < 30.0 ? UnityEngine.Random.Range(30f, 120f) : 0.0;
                _nextFireAt = Time.realtimeSinceStartup + UnityEngine.Random.Range(1f, 3f);
            }
            if (v.missionTime < _requiredMissionTime) return;

            if (Time.realtimeSinceStartup < _nextFireAt) return;

            // Warping with a warp-intolerant trap owed: drop to 1x now and let
            // the vessel unpack; the pick happens on a later frame.
            //
            // This is a bet, because eligibility cannot be tested while warping
            // — nearly every IsEligible requires !v.packed, and warp packs the
            // vessel. So drop warp, then watch: if no trap fires within
            // UnwarpGrace, what is owed cannot fire on this craft at all (an
            // uncrewed probe owing a Mandatory Spacewalk, an antenna-less craft
            // owing a Radio Silence) and we must stop fighting the player's
            // warp, or the mission becomes unflyable. A later fire re-opens the
            // bet, as does a new vessel.
            if (TimeWarp.CurrentRate > 1f && HasPendingNeedingUnwarp(scenario))
            {
                if (_unwarpAt <= _lastTrapActivityAt)
                {
                    _unwarpAt = Time.realtimeSinceStartup;   // something changed — bet again
                    Debug.Log("[KSP-AP] Trap: dropping warp to test owed traps");
                }
                if (Time.realtimeSinceStartup - _unwarpAt <= UnwarpGrace)
                {
                    TimeWarp.SetRate(0, true);
                    return;
                }
                _nextFireAt = Time.realtimeSinceStartup + FireStagger;
                return;   // bet lost — leave the warp alone
            }

            int slot = FindFireable(scenario, v, out ITrapActuator actuator);
            if (slot < 0)
            {
                // Nothing eligible: back off a tick rather than re-running every
                // IsEligible next frame. Several of them walk the whole part
                // list, and a backlog of traps that cannot fire on this craft is
                // the normal case (Spin Cycle while landed, and so on).
                _nextFireAt = Time.realtimeSinceStartup + FireStagger;
                return;
            }

            // End of a lull: one warning for the whole incoming wave, then the
            // wave lands unannounced. Red if anything fireable right now is
            // deadly; their longer pacing (Stage Fright's fuse) is the
            // actuator's own business.
            if (_forceWarn || Time.realtimeSinceStartup - _lastTrapActivityAt > LullSeconds)
            {
                float quiet = Time.realtimeSinceStartup - _lastTrapActivityAt;
                bool forced = _forceWarn;
                bool deadly = AnyFireableIsDeadly(scenario, v);
                _forceWarn = false;
                _lastTrapActivityAt = Time.realtimeSinceStartup;
                _nextFireAt = Time.realtimeSinceStartup + UnityEngine.Random.Range(2f, 3f);
                ScreenMessages.PostScreenMessage(
                    deadly
                        ? "<color=red>AP:</color> Something feels... very wrong."
                        : "<color=orange>AP:</color> Something feels... off.",
                    3f, ScreenMessageStyle.UPPER_CENTER);
                Debug.Log($"[KSP-AP] Trap warning posted (deadly={deadly}, "
                    + (forced ? "forced by vessel/scene change)" : $"quiet for {quiet:F1}s)"));
                return;
            }

            FireNow(mod, scenario, slot, actuator, v, runner);
        }

        /// <summary>
        /// First trap in arrival order that can fire on this vessel right now,
        /// as a slot in PendingTraps, or -1. Prunes entries whose fire already
        /// happened. Ineligible traps are SKIPPED, never blocking: they stay
        /// pending for a later vessel and are never consumed. A null vessel
        /// means the Space Center / Tracking Station drain, which only
        /// considers vessel-free traps.
        ///
        /// The returned slot is only valid for the current frame — the caller
        /// must consume it before anything else touches PendingTraps.
        /// </summary>
        private static int FindFireable(
            ApScenarioModule scenario, Vessel v, out ITrapActuator actuator)
        {
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
                if (!Registry.TryGetValue(pending.Name, out ITrapActuator candidate))
                    continue;   // actuator not built yet — stays queued
                if (v == null && !candidate.CanFireWithoutVessel) continue;
                if (!candidate.IsEligible(v)) continue;

                actuator = candidate;
                return i;
            }
            actuator = null;
            return -1;
        }

        /// <summary>Whether any trap that could fire right now is one of the
        /// scary ones — decides the tint of the lull warning, which covers a
        /// whole incoming wave rather than one named trap.</summary>
        private static bool AnyFireableIsDeadly(ApScenarioModule scenario, Vessel v)
        {
            foreach (PendingTrap pending in scenario.PendingTraps)
                if (DeadlyTrapNames.Contains(pending.Name)
                    && !FiredTrapStore.Contains(pending.Index)
                    && Registry.TryGetValue(pending.Name, out ITrapActuator actuator)
                    && (v != null || actuator.CanFireWithoutVessel)
                    && actuator.IsEligible(v))
                    return true;
            return false;
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
                // Instant traps register no effect, so the fire itself has to
                // count as activity — otherwise a run of them re-warns between
                // each one.
                _lastTrapActivityAt = Time.realtimeSinceStartup;
                _nextFireAt = Time.realtimeSinceStartup + FireStagger;
            }
        }

        /// <summary>
        /// Space Center / Tracking Station drain: fires pending vessel-free
        /// traps at the same cadence as flight, but with no arm/grace/warning —
        /// there is nothing to prep for without a craft on the line.
        /// </summary>
        private static void DrainVesselFree(KSPArchipelagoMod mod, ApScenarioModule scenario)
        {
            if (Time.realtimeSinceStartup < _nextFireAt) return;

            int slot = FindFireable(scenario, null, out ITrapActuator actuator);
            if (slot < 0)
            {
                _nextFireAt = Time.realtimeSinceStartup + FireStagger;
                return;
            }
            FireNow(mod, scenario, slot, actuator, null, EnsureRunner(mod));
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

        /// <summary>Leaving flight (or losing the vessel) ends any wave: the
        /// next trap warns again however recently one fired.</summary>
        private static void ResetFlightState()
        {
            _armedVesselId = uint.MaxValue;
            _forceWarn = true;
            _unwarpAt = float.NegativeInfinity;
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
