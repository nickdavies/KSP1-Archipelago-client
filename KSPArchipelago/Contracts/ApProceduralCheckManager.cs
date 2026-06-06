using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// Owns the per-type pools of AP location names that procedural
    /// contracts consume on completion. Loaded from slot_data
    /// ("procedural_contract_locations") at AP connect.
    ///
    /// Lifecycle of a name:
    ///   - Free: in the pool, no contract holds it, AP server hasn't checked it.
    ///   - Reserved: an offered/active procedural contract holds it as its
    ///     ChosenApLocationName. Survives Generate -> OnAccepted -> OnCompleted.
    ///   - Consumed: contract completed and ReportLocation fired. AP server
    ///     marks the location checked; subsequent ReserveNext queries skip it.
    ///   - Released: contract was withdrawn/declined/failed before
    ///     completion. Name goes back to Free.
    ///
    /// Reservations are tracked in-memory only — on save/load the
    /// procedural contract's OnSave/OnLoad persists its ChosenApLocationName,
    /// and ReBindAtLoad re-asserts the reservation when the contract is
    /// rehydrated.
    /// </summary>
    public static class ApProceduralCheckManager
    {
        // contract_type -> ordered list of AP location names
        private static readonly Dictionary<string, List<string>> _pools
            = new Dictionary<string, List<string>>();

        // contract_type -> set of names currently reserved by live contracts
        private static readonly Dictionary<string, HashSet<string>> _reserved
            = new Dictionary<string, HashSet<string>>();

        /// <summary>
        /// Replace the pools with new ones (called on AP connect). Reserves
        /// are NOT cleared — live contracts still hold their names. If a
        /// slot_data refresh removes a name that's currently reserved, the
        /// contract will fail to ReportLocation on complete (warning logged).
        /// </summary>
        public static void SetPools(JToken token)
        {
            _pools.Clear();
            if (token == null || token.Type == JTokenType.Null)
            {
                Debug.Log("[KSP-AP] ApProceduralCheckManager: no pools (slot_data field absent)");
                return;
            }
            if (token.Type != JTokenType.Object)
                throw new FormatException(
                    "procedural_contract_locations must be an object");

            int total = 0;
            foreach (var typeEntry in (JObject)token)
            {
                if (!(typeEntry.Value is JArray arr))
                    throw new FormatException(
                        $"procedural_contract_locations['{typeEntry.Key}'] must be array");
                var list = new List<string>(arr.Count);
                foreach (JToken n in arr)
                {
                    string name = (string)n;
                    if (!string.IsNullOrEmpty(name)) list.Add(name);
                }
                _pools[typeEntry.Key] = list;
                total += list.Count;
            }
            Debug.Log($"[KSP-AP] ApProceduralCheckManager: loaded {total} AP location(s) "
                    + $"across {_pools.Count} contract type(s)");
        }

        /// <summary>
        /// Reserve and return the next unchecked, unreserved AP location
        /// from the pool for this contract_type. Returns null if no
        /// names are available (either pool exhausted or all reserved /
        /// checked). A null result is a valid state — the contract still
        /// spawns but won't fire an AP check on completion.
        /// </summary>
        public static string ReserveNext(string contractType)
        {
            if (!_pools.TryGetValue(contractType, out var list)) return null;
            var reserved = GetReserved(contractType);
            foreach (string name in list)
            {
                if (reserved.Contains(name)) continue;
                if (ApContractSlotManager.IsApLocationChecked(name)) continue;
                reserved.Add(name);
                return name;
            }
            return null;
        }

        /// <summary>
        /// Release a reservation back to free. Called from procedural
        /// contracts on Withdraw/Decline/Fail.
        /// </summary>
        public static void ReleaseReservation(string contractType, string apLocationName)
        {
            if (string.IsNullOrEmpty(apLocationName)) return;
            GetReserved(contractType).Remove(apLocationName);
        }

        /// <summary>
        /// Final state — the AP server has now been told this location is
        /// checked. Clear the reservation; the IsApLocationChecked guard
        /// in ReserveNext will keep us from offering it again.
        /// </summary>
        public static void ConfirmConsumed(string contractType, string apLocationName)
        {
            if (string.IsNullOrEmpty(apLocationName)) return;
            GetReserved(contractType).Remove(apLocationName);
        }

        /// <summary>
        /// Re-assert a reservation for a procedural contract loaded from
        /// a save game. Idempotent — second call for same name is a no-op.
        /// Called from ApProceduralContract.OnLoad rehydrate hooks.
        /// </summary>
        public static void ReBindAtLoad(string contractType, string apLocationName)
        {
            if (string.IsNullOrEmpty(apLocationName)) return;
            GetReserved(contractType).Add(apLocationName);
        }

        private static HashSet<string> GetReserved(string contractType)
        {
            if (!_reserved.TryGetValue(contractType, out var s))
            {
                s = new HashSet<string>();
                _reserved[contractType] = s;
            }
            return s;
        }
    }
}
