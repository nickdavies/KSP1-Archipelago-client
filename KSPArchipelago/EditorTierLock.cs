using UnityEngine;

namespace KSPArchipelago
{
    /// <summary>
    /// Blocks placement of tier-locked placeholder parts in the VAB/SPH editor.
    /// Placeholders are visible with the AP icon so the player knows what they
    /// have, but destroyed on pickup until the progressive tier arrives.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class EditorTierLock : MonoBehaviour
    {
        private void Start()
        {
            GameEvents.onEditorPartEvent.Add(OnPartEvent);
        }

        private void OnDestroy()
        {
            GameEvents.onEditorPartEvent.Remove(OnPartEvent);
        }

        private void OnPartEvent(ConstructionEventType eventType, Part part)
        {
            if (part?.partInfo == null) return;

            var tierLocked = KSPArchipelagoPartsManager.TierLockedParts;
            if (!tierLocked.TryGetValue(part.partInfo.name, out string realPartName))
                return;

            if (eventType == ConstructionEventType.PartCreated
                || eventType == ConstructionEventType.PartAttached)
            {
                var mod = FindObjectOfType<KSPArchipelagoMod>();
                string progName = mod?.GetPartProgressiveName(realPartName) ?? "???";
                int reqTier = mod?.GetPartRequiredTier(realPartName) ?? 0;

                AvailablePart realPart = PartLoader.getPartInfoByName(realPartName);
                string title = realPart?.title ?? realPartName;

                if (part.parent != null)
                    part.decouple();

                DestroyImmediate(part.gameObject);

                ScreenMessages.PostScreenMessage(
                    $"<color=orange>{title}</color> is tier-locked.\n"
                    + $"Requires <color=yellow>{progName} Tier {reqTier}</color>",
                    4f, ScreenMessageStyle.UPPER_CENTER);
            }
        }
    }
}
