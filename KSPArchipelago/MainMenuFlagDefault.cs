using System.Reflection;
using UnityEngine;

namespace KSPArchipelago
{
    /// <summary>
    /// Makes the AP flag the default selection in the main-menu "Start New Game"
    /// setup window, without stopping the player from choosing another flag.
    ///
    /// KSP stores the new-game flag in a private static field (MainMenu.fURL,
    /// behind the private static MainMenu.flagURL property). MainMenu.Start()
    /// seeds it from the public DefaultFlagURL field (prefab value
    /// "Squad/Flags/default"); the FlagBrowser overwrites it when the player
    /// picks a flag. We overwrite both, once per main-menu load: DefaultFlagURL
    /// so any later re-seed uses AP, and fURL so the value already seeded this
    /// load shows AP immediately. Because we set it exactly once (never per
    /// frame) and before the player can open the flag browser, a player's own
    /// choice is never clobbered.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    public class MainMenuFlagDefault : MonoBehaviour
    {
        // GameData-relative URL of the default AP flag. Shipped from
        // assets/Flags/AP_planets_drawn.png into the mod's FlagsAgency folder,
        // so the AP flags land in the flag browser's "Agency" tab.
        private const string ApFlagUrl = "KSPArchipelago/FlagsAgency/AP_planets_drawn";

        private bool applied;

        // Run from Update, not Start: Unity does not order Start() across
        // objects, but it runs every object's Start() before any Update() in a
        // frame. So by our first Update, MainMenu.Start() has already seeded
        // fURL and we deterministically overwrite it rather than racing it.
        private void Update()
        {
            if (applied) return;

            var menu = FindObjectOfType<MainMenu>();
            if (menu == null) return; // menu not constructed yet; retry next frame
            applied = true;

            if (GameDatabase.Instance?.GetTexture(ApFlagUrl, false) == null)
            {
                Debug.LogWarning(
                    $"[KSP-AP] flag '{ApFlagUrl}' not loaded; leaving default flag unchanged");
                return;
            }

            // Public field: a later MainMenu.Start() re-seed picks AP.
            menu.DefaultFlagURL = ApFlagUrl;

            // Private static backing field: overwrite the value seeded this load
            // so the setup window shows AP now. The property's get/set are also
            // private static, so we go straight to the field.
            FieldInfo fURL = typeof(MainMenu).GetField(
                "fURL", BindingFlags.NonPublic | BindingFlags.Static);
            if (fURL != null)
                fURL.SetValue(null, ApFlagUrl);
            else
                Debug.LogWarning("[KSP-AP] MainMenu.fURL not found; new-game flag default not applied");

            Debug.Log($"[KSP-AP] new-game flag defaulted to {ApFlagUrl}");
        }
    }
}
