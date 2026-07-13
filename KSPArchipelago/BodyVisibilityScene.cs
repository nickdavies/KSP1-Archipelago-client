using System.Collections;
using KSP.UI.Screens.Mapview;
using UnityEngine;

namespace KSPArchipelago
{
    /// <summary>
    /// Re-asserts the hidden-body set on each scene load. KSP rebuilds map nodes
    /// and the Tab-target list per scene, so <see cref="BodyUnlockManager"/>'s
    /// static state must be re-applied: the DiscoveryInfo + scaled-mesh + PQS
    /// layer on scene ready, and the map-node icon + camera-target layer once the
    /// map view's nodes exist (OnMapEntered, or directly in the tracking station
    /// whose scene is always a map view).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    public class BodyVisibilityScene : MonoBehaviour
    {
        private void Start()
        {
            StartCoroutine(ReapplyWhenReady());
            GameEvents.OnMapEntered.Add(HandleMapEntered);
            // The tracking station is a permanent map view; OnMapEntered doesn't
            // fire for it, so kick the map-layer refresh directly.
            if (HighLogic.LoadedScene == GameScenes.TRACKSTATION)
                HandleMapEntered();
        }

        private void OnDestroy()
        {
            GameEvents.OnMapEntered.Remove(HandleMapEntered);
        }

        private IEnumerator ReapplyWhenReady()
        {
            // A couple of frames for FlightGlobals / scaled space to settle.
            yield return null;
            yield return null;
            BodyUnlockManager.ReapplyAll();
        }

        private void HandleMapEntered()
        {
            StartCoroutine(RefreshMapWhenReady());
        }

        private IEnumerator RefreshMapWhenReady()
        {
            // The first ~5 map nodes are the KSC / launch-site markers; the body
            // nodes populate after (mirrors ResearchBodies' processMapNodes wait).
            while (MapNode.AllMapNodes != null && MapNode.AllMapNodes.Count <= 5)
                yield return null;
            for (int i = 0; i < 5; i++) yield return null;
            // Full re-assert (not just the map layer): entering map view can
            // reactivate scaled bodies, so re-hide the deep layer here too.
            BodyUnlockManager.ReapplyAll();
        }
    }
}
