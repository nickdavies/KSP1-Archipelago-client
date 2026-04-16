using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using UnityEngine;
using KSP.UI.Screens;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Models;

namespace KSPArchipelago
{
    /// <summary>
    /// A tech node that is parent-ready but locked behind a higher R&amp;D band.
    /// </summary>
    public struct LockedNodeInfo
    {
        public string NodeId;
        public int Band;

        public LockedNodeInfo(string nodeId, int band)
        {
            NodeId = nodeId;
            Band = band;
        }
    }

    /// <summary>
    /// Scouted item data for a single AP location slot in a tech node.
    /// Shared between TechTreeScout (produces) and PlaceholderManager (consumes).
    /// </summary>
    internal struct ScoutedSlot
    {
        public int Slot;
        public string ItemName;         // AP item name (used for PartLoader lookup)
        public string ItemDisplayName;  // human-readable display name
        public ItemFlags Flags;
        public string ReceiverName;
        public bool IsForSelf;
        public bool IsKspPart;          // true if item is a KSP part (exists in PartLoader)
    }

    /// <summary>
    /// Scouts AP items at purchasable tech tree nodes and populates them into
    /// the native R&D parts list via PlaceholderManager.
    /// Active only in the Space Centre scene; detects R&D open/close via RDController.Instance.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class TechTreeScout : MonoBehaviour
    {
        private struct LocMapping
        {
            public string NodeId;
            public int Slot;
        }

        private KSPArchipelagoMod mod;
        private bool rdWasOpen;
        private bool needsRescount;

        private readonly PlaceholderManager placeholderManager = new PlaceholderManager();

        // Cached scout results across R&D sessions. Only cleared on AP disconnect.
        // Keys are tech node IDs; values are scouted slot lists for that node.
        private volatile Dictionary<string, List<ScoutedSlot>> scoutedByNode =
            new Dictionary<string, List<ScoutedSlot>>();

        // Set of node IDs we've already scouted (avoids redundant network calls).
        private readonly HashSet<string> scoutedNodeIds = new HashSet<string>();

        // Flag: scout results arrived on callback thread, need main-thread update.
        private volatile bool pendingScoutUpdate;

        private void Start()
        {
            mod = FindObjectOfType<KSPArchipelagoMod>();
        }

        private void Update()
        {
            if (mod == null || !mod.IsConnected) return;

            bool rdOpen = RDController.Instance != null;

            if (rdOpen && !rdWasOpen)
                OnRDOpen();

            if (!rdOpen && rdWasOpen)
                OnRDClose();

            rdWasOpen = rdOpen;

            if (!rdOpen) return;

            if (needsRescount && IsTechTreeReady())
            {
                needsRescount = false;
                ScoutNewPurchasableNodes();
            }

            // Apply scout results that arrived from the callback thread.
            if (pendingScoutUpdate)
            {
                pendingScoutUpdate = false;
                placeholderManager.UpdateWithScoutData(scoutedByNode);
            }
        }

        private void OnRDOpen()
        {
            placeholderManager.Initialize();

            if (IsTechTreeReady())
            {
                PopulateAndScout();
            }
            else
            {
                // Tree not ready yet; wait for next Update.
                needsRescount = true;
            }
        }

        private void OnRDClose()
        {
            placeholderManager.ClearAllFromUI();
        }

        private void PopulateAndScout()
        {
            List<string> purchasableIds = FindPurchasableNodeIds(out var bandLocked);

            // Show locked indicators on nodes where parents are OK but R&D band is too low.
            placeholderManager.PopulateBandLockedNodes(bandLocked);

            if (purchasableIds.Count == 0) return;

            // Populate placeholders for all purchasable nodes.
            var session = mod.Session;
            if (session == null) return;
            var missing = new HashSet<long>(session.Locations.AllMissingLocations);
            placeholderManager.PopulateNodes(purchasableIds, missing, mod.TechSlotsPerNode, session);

            // If we have cached scout data, apply it immediately.
            if (scoutedByNode.Count > 0)
            {
                placeholderManager.RestoreToUI();
                placeholderManager.UpdateWithScoutData(scoutedByNode);
            }

            // Scout only nodes we haven't scouted yet.
            ScoutNewPurchasableNodes();
        }

        /// <summary>
        /// Returns true once RDController has loaded at least 75% of the tech nodes
        /// we track as AP locations. Guards against scouting before the tree is populated.
        /// </summary>
        private bool IsTechTreeReady()
        {
            var nodes = RDController.Instance?.nodes;
            if (nodes == null) return false;

            int matched = 0;
            foreach (RDNode node in nodes)
            {
                if (node?.tech != null && MissionTracker.TechDisplayNames.ContainsKey(node.tech.techID))
                    matched++;
            }

            int threshold = (int)(MissionTracker.TechDisplayNames.Count * 0.75);
            return matched >= threshold;
        }

        private void ScoutNewPurchasableNodes()
        {
            ArchipelagoSession session = mod.Session;
            if (session == null) return;

            try
            {
                List<string> purchasableIds = FindPurchasableNodeIds();

                // Filter to nodes we haven't scouted yet.
                var newNodeIds = new List<string>();
                foreach (string id in purchasableIds)
                {
                    if (!scoutedNodeIds.Contains(id))
                        newNodeIds.Add(id);
                }

                if (newNodeIds.Count == 0) return;

                // Also populate placeholders for any truly new purchasable nodes
                // (e.g. nodes that became purchasable after researching another).
                var missing = new HashSet<long>(session.Locations.AllMissingLocations);
                placeholderManager.PopulateNodes(newNodeIds, missing, mod.TechSlotsPerNode, session);

                // Map location IDs to node+slot, filtering to unchecked locations only.
                var locMap = new Dictionary<long, LocMapping>();
                var locationIds = new List<long>();
                string gameName = session.ConnectionInfo.Game;

                foreach (string nodeId in newNodeIds)
                {
                    if (!MissionTracker.TechDisplayNames.TryGetValue(nodeId, out string displayName))
                        continue;

                    int maxSlots = mod.TechSlotsPerNode;
                    for (int slot = 1; slot <= maxSlots; slot++)
                    {
                        long locId = session.Locations.GetLocationIdFromName(gameName, $"{displayName} {slot}");
                        if (locId < 0 || !missing.Contains(locId)) continue;

                        locationIds.Add(locId);
                        locMap[locId] = new LocMapping { NodeId = nodeId, Slot = slot };
                    }
                }

                if (locationIds.Count == 0)
                {
                    // Mark as scouted even if all locations already checked.
                    foreach (string id in newNodeIds)
                        scoutedNodeIds.Add(id);
                    return;
                }

                Debug.Log($"[KSP-AP] Scouting {locationIds.Count} locations across {newNodeIds.Count} new purchasable nodes");

                session.Locations.ScoutLocationsAsync(HintCreationPolicy.CreateAndAnnounceOnce, locationIds.ToArray())
                    .ContinueWith(task => OnScoutComplete(task, locMap, newNodeIds));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KSP-AP] ScoutNewPurchasableNodes failed: {ex}");
            }
        }

        private void OnScoutComplete(Task<Dictionary<long, ScoutedItemInfo>> task,
            Dictionary<long, LocMapping> locMap, List<string> scoutedIds)
        {
            try
            {
                if (task.IsFaulted)
                {
                    Debug.LogWarning($"[KSP-AP] Scout task failed: {task.Exception}");
                    return;
                }

                Dictionary<long, ScoutedItemInfo> results = task.Result;

                // Merge results into the cached dictionary.
                var merged = new Dictionary<string, List<ScoutedSlot>>(scoutedByNode);

                foreach (var kvp in results)
                {
                    if (!locMap.TryGetValue(kvp.Key, out LocMapping mapping))
                        continue;

                    ScoutedItemInfo info = kvp.Value;
                    string itemName = info.ItemName ?? info.ItemDisplayName ?? $"Item #{info.ItemId}";
                    bool isKspPart = PartLoader.getPartInfoByName(itemName) != null;

                    var slot = new ScoutedSlot
                    {
                        Slot = mapping.Slot,
                        ItemName = itemName,
                        ItemDisplayName = info.ItemDisplayName ?? itemName,
                        Flags = info.Flags,
                        ReceiverName = info.Player?.Name ?? "Unknown",
                        IsForSelf = info.IsReceiverRelatedToActivePlayer,
                        IsKspPart = isKspPart
                    };

                    if (!merged.TryGetValue(mapping.NodeId, out List<ScoutedSlot> list))
                    {
                        list = new List<ScoutedSlot>();
                        merged[mapping.NodeId] = list;
                    }
                    list.Add(slot);
                }

                foreach (var list in merged.Values)
                    list.Sort((a, b) => a.Slot.CompareTo(b.Slot));

                // Mark these nodes as scouted.
                foreach (string id in scoutedIds)
                    scoutedNodeIds.Add(id);

                // Atomic swap — main thread sees either old or new, never partial.
                scoutedByNode = merged;
                pendingScoutUpdate = true;
                Debug.Log($"[KSP-AP] Scout complete: {results.Count} items across {scoutedIds.Count} nodes");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KSP-AP] Scout callback error: {ex}");
            }
        }

        private List<string> FindPurchasableNodeIds()
        {
            return FindPurchasableNodeIds(out _);
        }

        private List<string> FindPurchasableNodeIds(out List<LockedNodeInfo> bandLocked)
        {
            var result = new List<string>();
            bandLocked = new List<LockedNodeInfo>();
            try
            {
                foreach (RDNode node in RDController.Instance.nodes)
                {
                    if (node?.tech == null) continue;
                    if (node.IsResearched) continue;

                    if (IsNodePurchasable(node, out int requiredBand))
                    {
                        result.Add(node.tech.techID);
                    }
                    else if (requiredBand > 0)
                    {
                        // Parents OK but R&D band too low — show locked indicator.
                        bandLocked.Add(new LockedNodeInfo(node.tech.techID, requiredBand));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KSP-AP] FindPurchasableNodeIds failed: {ex}");
            }
            return result;
        }

        /// <summary>
        /// Checks whether a node's prerequisite parents are satisfied and the
        /// player's R&D level permits access to this node's band.
        /// Respects AnyParentToUnlock: when true, only one parent must be researched.
        /// Returns the required band via out parameter (for locked-node indicators).
        /// </summary>
        private bool IsNodePurchasable(RDNode node)
        {
            return IsNodePurchasable(node, out _);
        }

        private bool IsNodePurchasable(RDNode node, out int requiredBand)
        {
            requiredBand = -1;

            if (node.parents == null || node.parents.Length == 0)
            {
                // Check R&D band even for root-adjacent nodes.
                return CheckRDBand(node, out requiredBand);
            }

            bool anyResearched = false;
            bool anyUnresearched = false;

            foreach (RDNode.Parent p in node.parents)
            {
                if (p?.parent?.node == null) continue;
                if (p.parent.node.IsResearched)
                    anyResearched = true;
                else
                    anyUnresearched = true;
            }

            bool parentsOk = node.AnyParentToUnlock
                ? (anyResearched || !anyUnresearched)
                : !anyUnresearched;

            if (!parentsOk) return false;

            return CheckRDBand(node, out requiredBand);
        }

        private bool CheckRDBand(RDNode node, out int requiredBand)
        {
            requiredBand = -1;
            if (mod?.NodeBands != null
                && mod.NodeBands.TryGetValue(node.tech.techID, out int band))
            {
                requiredBand = band;
                if (mod.RDLevel < band)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Called by MissionTracker after a tech node is purchased.
        /// Clears placeholders from the node and triggers re-scout for newly purchasable nodes.
        /// </summary>
        public void OnNodeChecked(string techId)
        {
            placeholderManager.ClearNode(techId);

            var current = scoutedByNode;
            if (current.ContainsKey(techId))
            {
                var copy = new Dictionary<string, List<ScoutedSlot>>(current);
                copy.Remove(techId);
                scoutedByNode = copy;
            }

            scoutedNodeIds.Remove(techId);
            needsRescount = true;
        }

        /// <summary>
        /// Called on AP disconnect. Clears all cached data.
        /// </summary>
        public void OnDisconnect()
        {
            placeholderManager.Reset();
            scoutedByNode = new Dictionary<string, List<ScoutedSlot>>();
            scoutedNodeIds.Clear();
            pendingScoutUpdate = false;
        }
    }
}
