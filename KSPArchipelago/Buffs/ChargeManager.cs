using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPArchipelago.Buffs
{
    /// <summary>
    /// Consumable buffs: received copies bank a charge the player spends when
    /// they choose, from the AP mod menu.
    /// </summary>
    /// <remarks>
    /// Player-triggered, not auto-firing, and that is a design decision rather
    /// than convenience. A forced Mid-Air Refuel mid-descent adds mass, wrecks
    /// TWR margins and shifts CoM — an unasked-for "buff" that can lose a
    /// craft. The timing has to be the player's.
    ///
    /// ACCOUNTING. Held = (copies in AllItemsReceived) − (indices recorded as
    /// spent). Both halves are derived, never accumulated:
    ///   * the received side is rebuilt by GiveItem replaying every item, with
    ///     ResetProgressiveState zeroing first (the double-count hazard its
    ///     comment documents);
    ///   * the spent side lives in ReceivedIndexStore.SpentCharges, whose
    ///     authoritative copy is on the AP server.
    /// So a reconnect recomputes the same number, and a save revert does NOT
    /// refund a spend — spending is permanent, exactly like suffering a trap.
    ///
    /// Received-item INDEX is the identity of a copy, which is why the two
    /// halves can be reconciled at all: spending marks the lowest index not
    /// already marked.
    /// </remarks>
    public static class ChargeManager
    {
        /// <summary>Received-item indices of every consumable copy, in receipt
        /// order. Rebuilt from scratch on every full replay.</summary>
        private static readonly List<int> _received = new List<int>();

        /// <summary>Item name -> the consumable it grants. Kept here rather
        /// than in BuffDefs because consumables are a different kind of thing:
        /// they are spent, not applied.</summary>
        public const string RefuelItemName = "Buff: Mid-Air Refuel";

        public static bool IsConsumableItem(string itemName)
        {
            return itemName == RefuelItemName;
        }

        /// <summary>Record one received copy at its item index.</summary>
        public static void NoteReceived(int itemIndex, string itemName)
        {
            if (!IsConsumableItem(itemName)) return;
            if (_received.Contains(itemIndex)) return;
            _received.Add(itemIndex);
            _received.Sort();
        }

        /// <summary>Zero the received side. Called from ResetProgressiveState
        /// before a full re-walk of AllItemsReceived.</summary>
        public static void ResetReceived()
        {
            _received.Clear();
        }

        /// <summary>
        /// Charges available to spend. Zero until the spent-store has both its
        /// file and its server merge — spending before then could double-spend
        /// a charge the server already knows about.
        /// </summary>
        public static int Available
        {
            get
            {
                if (!ReceivedIndexStore.SpentCharges.IsLoaded) return 0;
                int n = 0;
                for (int i = 0; i < _received.Count; i++)
                    if (!ReceivedIndexStore.SpentCharges.Contains(_received[i])) n++;
                return n;
            }
        }

        /// <summary>Total copies received, spent or not (for the UI).</summary>
        public static int TotalReceived { get { return _received.Count; } }

        /// <summary>
        /// Why a spend is currently impossible, or null if it can go ahead.
        /// Surfaced in the UI so an unavailable button explains itself rather
        /// than being silently greyed out.
        /// </summary>
        public static string BlockedReason(KSPArchipelagoMod mod)
        {
            if (mod == null || !mod.IsConnected) return "not connected";
            if (!ReceivedIndexStore.SpentCharges.IsLoaded) return "syncing with server";
            if (Available <= 0) return "no charges";
            if (!HighLogic.LoadedSceneIsFlight) return "flight only";
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || !v.loaded || v.packed) return "no active vessel";
            return null;
        }

        /// <summary>
        /// Spend one charge on the active vessel.
        /// </summary>
        /// <remarks>
        /// ATOMICITY: the actuator runs FIRST and reports whether it changed
        /// anything; the charge is only marked spent if it did. A charge
        /// consumed for no effect (already-full tanks, a craft with nothing
        /// refuelable) would be a silent theft of a reward, so the failure
        /// mode is "nothing happened, keep your charge" rather than
        /// "mark it spent and hope".
        /// </remarks>
        public static bool TrySpend(KSPArchipelagoMod mod)
        {
            string blocked = BlockedReason(mod);
            if (blocked != null)
            {
                Debug.Log($"[KSP-AP] Charge spend refused: {blocked}");
                return false;
            }

            int index = NextUnspentIndex();
            if (index < 0) return false;

            Vessel vessel = FlightGlobals.ActiveVessel;
            string summary;
            bool changed;
            try
            {
                changed = RefuelAction.Run(vessel, out summary);
            }
            catch (Exception e)
            {
                Debug.LogError($"[KSP-AP] Refuel threw, charge NOT spent: {e}");
                ScreenMessages.PostScreenMessage(
                    "AP: refuel failed — charge not spent", 5f, ScreenMessageStyle.UPPER_CENTER);
                return false;
            }

            if (!changed)
            {
                ScreenMessages.PostScreenMessage(
                    $"AP: nothing to refuel ({summary}) — charge not spent",
                    5f, ScreenMessageStyle.UPPER_CENTER);
                return false;
            }

            // Effect landed: burn the charge and flush immediately, so a crash
            // between here and the next save cannot resurrect it. Same
            // crash-safety rule the trap store follows at fire time.
            ReceivedIndexStore.SpentCharges.Record(index);
            mod.PushStoreIndex(ReceivedIndexStore.SpentCharges, index);

            ScreenMessages.PostScreenMessage(
                $"AP: Mid-Air Refuel — {summary}", 6f, ScreenMessageStyle.UPPER_CENTER);
            Debug.Log($"[KSP-AP] Spent refuel charge (item index {index}): {summary}; "
                      + $"{Available} left");
            return true;
        }

        private static int NextUnspentIndex()
        {
            for (int i = 0; i < _received.Count; i++)
                if (!ReceivedIndexStore.SpentCharges.Contains(_received[i]))
                    return _received[i];
            return -1;
        }
    }
}
