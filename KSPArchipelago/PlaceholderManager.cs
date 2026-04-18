using System.Collections.Generic;

using UnityEngine;
using KSP.UI.Screens;

namespace KSPArchipelago
{
    /// <summary>
    /// Manages placeholder parts in tech tree nodes to show scouted AP items.
    /// Populates RDTech.partsAssigned with mystery goo placeholders for unchecked
    /// locations, then swaps in real AvailablePart objects when scout data arrives.
    /// </summary>
    internal class PlaceholderManager
    {
        private const string PlaceholderPrefix = "ap.placeholder.";

        // Cached placeholder AvailableParts from PartLoader, indexed 0..N-1.
        // Pool size is determined by how many ap_placeholder_NNN parts exist
        // in the cfg — no hardcoded cap in code.
        private readonly List<AvailablePart> pool = new List<AvailablePart>();

        // Which placeholders are currently in use: placeholder index → (nodeId, slot).
        private readonly Dictionary<int, string> inUseBy = new Dictionary<int, string>();

        // Per-node tracking: nodeId → list of (slot index, AvailablePart we inserted).
        // The AvailablePart may be a placeholder or a real part after scout update.
        private readonly Dictionary<string, List<SlotEntry>> nodeEntries =
            new Dictionary<string, List<SlotEntry>>();

        // Original science costs for band-locked nodes (nodeId → original cost).
        // Used to restore costs when the node becomes unlockable.
        private readonly Dictionary<string, int> originalCosts =
            new Dictionary<string, int>();

        private struct SlotEntry
        {
            public int Slot;               // AP slot number (1-based)
            public AvailablePart Part;      // what's in partsAssigned
            public int PlaceholderIndex;    // index into pool, or -1 if real part
        }

        private bool initialized;

        /// <summary>
        /// Cache placeholder AvailableParts from PartLoader. Call once after
        /// PartLoader has finished (e.g. on first R&D open).
        /// </summary>
        public void Initialize()
        {
            if (initialized) return;

            pool.Clear();
            for (int i = 1; ; i++)
            {
                string name = $"{PlaceholderPrefix}{i:D3}";
                AvailablePart ap = PartLoader.getPartInfoByName(name);
                if (ap == null) break;
                pool.Add(ap);
            }

            initialized = true;
            Debug.Log($"[KSP-AP] PlaceholderManager: cached {pool.Count} placeholder parts");
        }

        /// <summary>
        /// Populate purchasable, non-researched tech nodes with mystery goo placeholders
        /// for each unchecked AP location slot. Call when R&D opens.
        /// </summary>
        /// <param name="purchasableNodeIds">Node IDs that are purchasable right now.</param>
        /// <param name="missingLocations">Set of unchecked AP location IDs.</param>
        /// <param name="slotsPerNode">Number of AP slots per tech node.</param>
        /// <param name="session">AP session for location ID lookups.</param>
        public void PopulateNodes(
            List<string> purchasableNodeIds,
            HashSet<long> missingLocations,
            int slotsPerNode,
            Archipelago.MultiClient.Net.ArchipelagoSession session)
        {
            if (!initialized || pool.Count == 0) return;
            if (RDController.Instance == null) return;

            string gameName = session.ConnectionInfo.Game;
            int assigned = 0;

            foreach (string nodeId in purchasableNodeIds)
            {
                // Skip nodes we've already populated (cached from previous R&D open).
                if (nodeEntries.ContainsKey(nodeId)) continue;

                if (!MissionTracker.TechDisplayNames.TryGetValue(nodeId, out string displayName))
                    continue;

                // Find the RDNode for this tech ID.
                RDNode rdNode = FindRDNode(nodeId);
                if (rdNode?.tech == null) continue;

                var entries = new List<SlotEntry>();

                for (int slot = 1; slot <= slotsPerNode; slot++)
                {
                    long locId = session.Locations.GetLocationIdFromName(
                        gameName, $"{displayName} {slot}");
                    if (locId < 0 || !missingLocations.Contains(locId))
                        continue;

                    // Find next available placeholder (skip those already in use).
                    int placeholderIdx = -1;
                    for (int i = 0; i < pool.Count; i++)
                    {
                        if (!inUseBy.ContainsKey(i))
                        {
                            placeholderIdx = i;
                            break;
                        }
                    }
                    if (placeholderIdx < 0)
                    {
                        Debug.LogWarning("[KSP-AP] PlaceholderManager: ran out of placeholders");
                        break;
                    }

                    AvailablePart placeholder = pool[placeholderIdx];
                    // Reset title/description to defaults before inserting.
                    placeholder.title = "AP Item";
                    placeholder.description = "An Archipelago multiworld item.";

                    rdNode.tech.partsAssigned.Add(placeholder);
                    inUseBy[placeholderIdx] = nodeId;

                    entries.Add(new SlotEntry
                    {
                        Slot = slot,
                        Part = placeholder,
                        PlaceholderIndex = placeholderIdx
                    });
                    assigned++;
                }

                if (entries.Count > 0)
                    nodeEntries[nodeId] = entries;
            }

            Debug.Log($"[KSP-AP] PlaceholderManager: populated {assigned} slots across {nodeEntries.Count} nodes");
        }

        /// <summary>
        /// Add a single "Locked" placeholder to nodes where parents are satisfied but
        /// the player's R&D level is too low. Tells the player why the node is empty.
        /// </summary>
        public void PopulateBandLockedNodes(List<LockedNodeInfo> lockedNodes)
        {
            if (!initialized || pool.Count == 0) return;
            if (RDController.Instance == null) return;

            foreach (var info in lockedNodes)
            {
                // Skip if already populated (purchasable or previously locked).
                if (nodeEntries.ContainsKey(info.NodeId)) continue;

                RDNode rdNode = FindRDNode(info.NodeId);
                if (rdNode?.tech == null) continue;

                int placeholderIdx = -1;
                for (int i = 0; i < pool.Count; i++)
                {
                    if (!inUseBy.ContainsKey(i))
                    {
                        placeholderIdx = i;
                        break;
                    }
                }
                if (placeholderIdx < 0) break;

                AvailablePart placeholder = pool[placeholderIdx];
                placeholder.title = $"Locked \u2014 Requires R&D Level {info.Band}";
                placeholder.description = "Find Progressive R&D items in the multiworld to unlock higher tech tiers.";

                rdNode.tech.partsAssigned.Add(placeholder);
                inUseBy[placeholderIdx] = info.NodeId;

                // Prevent purchase by setting science cost impossibly high.
                if (!originalCosts.ContainsKey(info.NodeId))
                    originalCosts[info.NodeId] = rdNode.tech.scienceCost;
                rdNode.tech.scienceCost = int.MaxValue;

                nodeEntries[info.NodeId] = new List<SlotEntry>
                {
                    new SlotEntry { Slot = 0, Part = placeholder, PlaceholderIndex = placeholderIdx }
                };
            }
        }

        /// <summary>
        /// Update populated nodes with scout data. For same-player KSP parts, swap the
        /// placeholder with the real AvailablePart. For everything else, update the
        /// placeholder's title and description. Call on main thread when scout completes.
        /// </summary>
        public void UpdateWithScoutData(Dictionary<string, List<ScoutedSlot>> scoutedByNode)
        {
            if (!initialized) return;

            int updated = 0;
            foreach (var kvp in scoutedByNode)
            {
                string nodeId = kvp.Key;
                if (!nodeEntries.TryGetValue(nodeId, out List<SlotEntry> entries))
                    continue;

                RDNode rdNode = FindRDNode(nodeId);
                if (rdNode?.tech == null) continue;

                foreach (ScoutedSlot scouted in kvp.Value)
                {
                    // Find the entry for this slot.
                    int entryIdx = entries.FindIndex(e => e.Slot == scouted.Slot);
                    if (entryIdx < 0) continue;

                    SlotEntry entry = entries[entryIdx];

                    if (scouted.IsForSelf && scouted.IsKspPart)
                    {
                        AvailablePart realPart = PartLoader.getPartInfoByName(scouted.ItemName);
                        if (realPart != null)
                        {
                            // Check if this part would be tier-locked. If so, keep the
                            // placeholder and annotate it rather than swapping in the
                            // real part (which has no lock indication).
                            var mod = UnityEngine.Object.FindObjectOfType<KSPArchipelagoMod>();
                            if (mod != null && mod.IsPartTierLocked(scouted.ItemName))
                            {
                                string progName = mod.GetPartProgressiveName(scouted.ItemName);
                                int reqTier = mod.GetPartRequiredTier(scouted.ItemName);
                                entry.Part.title = $"{realPart.title} (tier locked)";
                                entry.Part.description = $"Requires {progName} Tier {reqTier}";
                                updated++;
                                continue;
                            }

                            // Not tier-locked — swap placeholder for the real part.
                            int listIdx = rdNode.tech.partsAssigned.IndexOf(entry.Part);
                            if (listIdx >= 0)
                                rdNode.tech.partsAssigned[listIdx] = realPart;

                            // Free the placeholder back to pool.
                            if (entry.PlaceholderIndex >= 0)
                                inUseBy.Remove(entry.PlaceholderIndex);

                            entry.Part = realPart;
                            entry.PlaceholderIndex = -1;
                            entries[entryIdx] = entry;
                            updated++;
                            continue;
                        }
                    }

                    // Non-part or other player: update placeholder metadata.
                    string label = scouted.IsForSelf
                        ? scouted.ItemDisplayName
                        : $"{scouted.ItemDisplayName} (for {scouted.ReceiverName})";
                    entry.Part.title = label;
                    entry.Part.description = ClassificationDescription(scouted.Flags);
                    updated++;
                }
            }

            // Refresh all populated nodes so part count tooltips and part lists update.
            RefreshAllPopulatedNodes();

            Debug.Log($"[KSP-AP] PlaceholderManager: updated {updated} slots with scout data");
        }

        /// <summary>
        /// Remove all our entries from a specific node (called when node is researched).
        /// </summary>
        public void ClearNode(string nodeId)
        {
            if (!nodeEntries.TryGetValue(nodeId, out List<SlotEntry> entries))
                return;

            RDNode rdNode = FindRDNode(nodeId);
            if (rdNode?.tech != null)
            {
                foreach (SlotEntry entry in entries)
                    rdNode.tech.partsAssigned.Remove(entry.Part);

                // Restore original science cost if we inflated it.
                if (originalCosts.TryGetValue(nodeId, out int cost))
                {
                    rdNode.tech.scienceCost = cost;
                    originalCosts.Remove(nodeId);
                }
            }

            // Free placeholders back to pool.
            foreach (SlotEntry entry in entries)
            {
                if (entry.PlaceholderIndex >= 0)
                    inUseBy.Remove(entry.PlaceholderIndex);
            }

            nodeEntries.Remove(nodeId);
        }

        /// <summary>
        /// Remove all entries from all nodes (called on R&D close).
        /// Does NOT clear nodeEntries — we keep the structural data so
        /// PopulateNodes can skip already-populated nodes on reopen.
        /// The actual partsAssigned lists are rebuilt by KSP on R&D reopen.
        /// </summary>
        public void ClearAllFromUI()
        {
            if (RDController.Instance != null)
            {
                foreach (var kvp in nodeEntries)
                {
                    RDNode rdNode = FindRDNode(kvp.Key);
                    if (rdNode?.tech == null) continue;
                    foreach (SlotEntry entry in kvp.Value)
                        rdNode.tech.partsAssigned.Remove(entry.Part);
                }
            }

            inUseBy.Clear();
        }

        /// <summary>
        /// Re-insert cached entries into partsAssigned after R&D reopens.
        /// KSP rebuilds partsAssigned from scratch on R&D open, so our
        /// previous insertions are gone. This restores them.
        /// </summary>
        public void RestoreToUI()
        {
            if (RDController.Instance == null) return;

            var toRemove = new List<string>();

            foreach (var kvp in nodeEntries)
            {
                RDNode rdNode = FindRDNode(kvp.Key);
                if (rdNode?.tech == null || rdNode.IsResearched)
                {
                    toRemove.Add(kvp.Key);
                    continue;
                }

                foreach (SlotEntry entry in kvp.Value)
                {
                    if (!rdNode.tech.partsAssigned.Contains(entry.Part))
                        rdNode.tech.partsAssigned.Add(entry.Part);

                    // Re-track placeholder usage so new nodes don't double-assign.
                    if (entry.PlaceholderIndex >= 0)
                        inUseBy[entry.PlaceholderIndex] = kvp.Key;
                }
            }

            // Clean up entries for nodes that have been researched since last open.
            foreach (string nodeId in toRemove)
            {
                foreach (SlotEntry entry in nodeEntries[nodeId])
                {
                    if (entry.PlaceholderIndex >= 0)
                        inUseBy.Remove(entry.PlaceholderIndex);
                }
                nodeEntries.Remove(nodeId);
            }
        }

        /// <summary>
        /// Clear all band-locked placeholder entries (Slot == 0) from both UI and cache.
        /// Called when R&D level changes so nodes can be re-classified as purchasable.
        /// </summary>
        public void ClearBandLockedNodes()
        {
            var ids = new List<string>();
            foreach (var kvp in nodeEntries)
            {
                if (kvp.Value.Count == 1 && kvp.Value[0].Slot == 0)
                    ids.Add(kvp.Key);
            }
            foreach (string nodeId in ids)
                ClearNode(nodeId);
        }

        /// <summary>
        /// Full reset — clear everything including cached data. Call on AP disconnect.
        /// </summary>
        public void Reset()
        {
            ClearAllFromUI();
            nodeEntries.Clear();
            inUseBy.Clear();
            originalCosts.Clear();
            initialized = false;
        }

        /// <summary>
        /// Returns the set of node IDs we've already populated (for skip logic in scouting).
        /// </summary>
        public HashSet<string> GetPopulatedNodeIds()
        {
            return new HashSet<string>(nodeEntries.Keys);
        }

        private static RDNode FindRDNode(string techId)
        {
            if (RDController.Instance?.nodes == null) return null;
            foreach (RDNode node in RDController.Instance.nodes)
            {
                if (node?.tech?.techID == techId)
                    return node;
            }
            return null;
        }

        private static string ClassificationDescription(
            Archipelago.MultiClient.Net.Enums.ItemFlags flags)
        {
            if ((flags & Archipelago.MultiClient.Net.Enums.ItemFlags.Advancement) != 0)
                return "Classification: Advancement (important for progression)";
            if ((flags & Archipelago.MultiClient.Net.Enums.ItemFlags.NeverExclude) != 0)
                return "Classification: Useful";
            if ((flags & Archipelago.MultiClient.Net.Enums.ItemFlags.Trap) != 0)
                return "Classification: Trap!";
            return "Classification: Filler";
        }

        /// <summary>
        /// Refresh graphics on all nodes we've populated, updating part count
        /// tooltips and the selected node's parts list.
        /// </summary>
        public void RefreshAllPopulatedNodes()
        {
            if (RDController.Instance == null) return;
            try
            {
                foreach (string nodeId in nodeEntries.Keys)
                {
                    RDNode rdNode = FindRDNode(nodeId);
                    rdNode?.UpdateGraphics();
                }
            }
            catch { /* R&D may be closing */ }
        }
    }
}
