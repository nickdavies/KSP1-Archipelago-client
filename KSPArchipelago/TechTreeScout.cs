using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using UnityEngine;
using KSP.UI.Screens;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Models;

using Color = UnityEngine.Color;

namespace KSPArchipelago
{
    /// <summary>
    /// Scouts AP items at purchasable tech tree nodes and displays them in an IMGUI overlay.
    /// Active only in the Space Centre scene; detects R&D open/close via RDController.Instance.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class TechTreeScout : MonoBehaviour
    {
        private struct ScoutedSlot
        {
            public int Slot;
            public string ItemDisplayName;
            public ItemFlags Flags;
            public string ReceiverName;
            public bool IsForSelf;
        }

        private struct LocMapping
        {
            public string NodeId;
            public int Slot;
        }

        private KSPArchipelagoMod mod;
        private bool rdWasOpen;
        private bool hasScoutedThisSession;
        private bool needsRescount;

        // Swapped atomically from callback thread. Main thread reads the reference.
        private volatile Dictionary<string, List<ScoutedSlot>> scoutedByNode =
            new Dictionary<string, List<ScoutedSlot>>();

        private string selectedNodeId;

        // IMGUI
        private Rect panelRect = new Rect(20, 200, 280, 0);
        private GUIStyle advancementStyle;
        private GUIStyle usefulStyle;
        private GUIStyle trapStyle;
        private GUIStyle fillerStyle;
        private bool stylesInitialized;

        private void Start()
        {
            mod = FindObjectOfType<KSPArchipelagoMod>();
        }

        private void Update()
        {
            if (mod == null || !mod.IsConnected) return;

            bool rdOpen = RDController.Instance != null;

            if (rdOpen && !rdWasOpen)
            {
                hasScoutedThisSession = false;
                needsRescount = false;
            }

            if (!rdOpen && rdWasOpen)
            {
                hasScoutedThisSession = false;
                selectedNodeId = null;
            }

            rdWasOpen = rdOpen;

            if (!rdOpen) return;

            if ((!hasScoutedThisSession || needsRescount) && IsTechTreeReady())
            {
                needsRescount = false;
                hasScoutedThisSession = true;
                ScoutPurchasableNodes();
            }

            UpdateSelectedNode();
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

        private void ScoutPurchasableNodes()
        {
            ArchipelagoSession session = mod.Session;
            if (session == null) return;

            try
            {
                List<string> purchasableIds = FindPurchasableNodeIds();
                if (purchasableIds.Count == 0)
                {
                    Debug.Log("[KSP-AP] No purchasable tech nodes to scout.");
                    return;
                }

                // Map location IDs to node+slot, filtering to unchecked locations only.
                var locMap = new Dictionary<long, LocMapping>();
                var locationIds = new List<long>();
                var missing = new HashSet<long>(session.Locations.AllMissingLocations);
                string gameName = session.ConnectionInfo.Game;

                foreach (string nodeId in purchasableIds)
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
                    Debug.Log("[KSP-AP] All purchasable node locations already checked.");
                    return;
                }

                Debug.Log($"[KSP-AP] Scouting {locationIds.Count} locations across {purchasableIds.Count} purchasable nodes");

                // CreateAndAnnounceOnce: creates a free hint (no point cost) on first scout,
                // skips if the hint already exists. Other players see where their items are.
                session.Locations.ScoutLocationsAsync(HintCreationPolicy.CreateAndAnnounceOnce, locationIds.ToArray())
                    .ContinueWith(task => OnScoutComplete(task, locMap));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KSP-AP] ScoutPurchasableNodes failed: {ex}");
            }
        }

        private void OnScoutComplete(Task<Dictionary<long, ScoutedItemInfo>> task,
            Dictionary<long, LocMapping> locMap)
        {
            try
            {
                if (task.IsFaulted)
                {
                    Debug.LogWarning($"[KSP-AP] Scout task failed: {task.Exception}");
                    return;
                }

                Dictionary<long, ScoutedItemInfo> results = task.Result;
                var newDict = new Dictionary<string, List<ScoutedSlot>>();

                foreach (var kvp in results)
                {
                    LocMapping mapping;
                    if (!locMap.TryGetValue(kvp.Key, out mapping))
                        continue;

                    ScoutedItemInfo info = kvp.Value;
                    var slot = new ScoutedSlot
                    {
                        Slot = mapping.Slot,
                        ItemDisplayName = info.ItemDisplayName ?? $"Item #{info.ItemId}",
                        Flags = info.Flags,
                        ReceiverName = info.Player?.Name ?? "Unknown",
                        IsForSelf = info.IsReceiverRelatedToActivePlayer
                    };

                    List<ScoutedSlot> list;
                    if (!newDict.TryGetValue(mapping.NodeId, out list))
                    {
                        list = new List<ScoutedSlot>();
                        newDict[mapping.NodeId] = list;
                    }
                    list.Add(slot);
                }

                foreach (var list in newDict.Values)
                    list.Sort((a, b) => a.Slot.CompareTo(b.Slot));

                // Atomic swap — main thread sees either old or new, never partial.
                scoutedByNode = newDict;
                Debug.Log($"[KSP-AP] Scout complete: {results.Count} items across {newDict.Count} nodes");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KSP-AP] Scout callback error: {ex}");
            }
        }

        private List<string> FindPurchasableNodeIds()
        {
            var result = new List<string>();
            try
            {
                foreach (RDNode node in RDController.Instance.nodes)
                {
                    if (node?.tech == null) continue;
                    if (node.IsResearched) continue;

                    if (IsNodePurchasable(node))
                        result.Add(node.tech.techID);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KSP-AP] FindPurchasableNodeIds failed: {ex}");
            }
            return result;
        }

        /// <summary>
        /// Checks whether a node's prerequisite parents are satisfied.
        /// Respects AnyParentToUnlock: when true, only one parent must be researched.
        /// </summary>
        private static bool IsNodePurchasable(RDNode node)
        {
            if (node.parents == null || node.parents.Length == 0)
                return true;

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

            if (node.AnyParentToUnlock)
                return anyResearched || !anyUnresearched;
            else
                return !anyUnresearched;
        }

        private void UpdateSelectedNode()
        {
            try
            {
                selectedNodeId = RDController.Instance?.node_selected?.tech?.techID;
            }
            catch
            {
                selectedNodeId = null;
            }
        }

        /// <summary>
        /// Called by MissionTracker after a tech node is purchased.
        /// Removes cached scout data and triggers re-scout for newly purchasable nodes.
        /// </summary>
        public void OnNodeChecked(string techId)
        {
            var current = scoutedByNode;
            if (current.ContainsKey(techId))
            {
                var copy = new Dictionary<string, List<ScoutedSlot>>(current);
                copy.Remove(techId);
                scoutedByNode = copy;
            }
            needsRescount = true;
        }

        // ----------------------------------------------------------------
        // IMGUI overlay
        // ----------------------------------------------------------------

        private void OnGUI()
        {
            if (selectedNodeId == null) return;

            var dict = scoutedByNode;
            List<ScoutedSlot> slots;
            if (!dict.TryGetValue(selectedNodeId, out slots)) return;

            if (!stylesInitialized) InitStyles();

            panelRect = GUILayout.Window(
                0xA9C1A61,
                panelRect,
                DrawScoutPanel,
                "Scouted Items",
                GUILayout.Width(280));
        }

        private void InitStyles()
        {
            advancementStyle = new GUIStyle(GUI.skin.label);
            advancementStyle.normal.textColor = new Color(0.7f, 0.4f, 1f);

            usefulStyle = new GUIStyle(GUI.skin.label);
            usefulStyle.normal.textColor = new Color(0.4f, 0.6f, 1f);

            trapStyle = new GUIStyle(GUI.skin.label);
            trapStyle.normal.textColor = new Color(1f, 0.3f, 0.3f);

            fillerStyle = new GUIStyle(GUI.skin.label);
            fillerStyle.normal.textColor = Color.gray;

            stylesInitialized = true;
        }

        private void DrawScoutPanel(int id)
        {
            var dict = scoutedByNode;
            List<ScoutedSlot> slots;
            if (!dict.TryGetValue(selectedNodeId, out slots))
            {
                GUILayout.Label("No data.");
                GUI.DragWindow();
                return;
            }

            foreach (ScoutedSlot slot in slots)
            {
                GUIStyle style = StyleForFlags(slot.Flags);
                string label = slot.IsForSelf
                    ? slot.ItemDisplayName
                    : $"{slot.ItemDisplayName} (for {slot.ReceiverName})";
                GUILayout.Label(label, style);
            }

            GUI.DragWindow();
        }

        private GUIStyle StyleForFlags(ItemFlags flags)
        {
            if ((flags & ItemFlags.Advancement) != 0) return advancementStyle;
            if ((flags & ItemFlags.NeverExclude) != 0) return usefulStyle;
            if ((flags & ItemFlags.Trap) != 0) return trapStyle;
            return fillerStyle;
        }
    }
}
