using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace Seamless
{
    /// <summary>The only class touching Harmony, so the mod still loads (arrivals only) without it.</summary>
    internal static class HarmonyHook
    {
        /// <summary>The game's scene loads are hooked, so a preloaded scene can be finished.</summary>
        internal static bool PreloadReady;

        internal static int TextureLoaderDeferrals;

        public static void Install()
        {
            var harmony = new Harmony("Seamless.Transitions");
            var target = AccessTools.Method(typeof(HighLogic), nameof(HighLogic.LoadScene), new[] { typeof(GameScenes) });
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(HarmonyHook), nameof(LoadScenePrefix)));

            try
            {
                var buffered = AccessTools.Method(typeof(HighLogic), nameof(HighLogic.bufferedLoad), new[] { typeof(int), typeof(bool) });
                harmony.Patch(buffered, prefix: new HarmonyMethod(typeof(HarmonyHook), nameof(BufferedLoadPrefix)));
                // Where HighLogic.LoadScene loads the scene itself (KSP's unbuffered switches, every
                // switch on Retooled) bufferedLoad never runs: that call is ours as well.
                harmony.Patch(target, transpiler: new HarmonyMethod(typeof(HarmonyHook), nameof(LoadSceneTranspiler)));
                PreloadReady = true;
            }
            catch (Exception e) { Log.Warn("scene preload unavailable: " + e.Message); }

            // While a transition covers the screen, KSP's own ShowUI calls (its loading screen ending,
            // the flight scene starting) are held back: re-hiding the HUD a frame later let it flash.
            try
            {
                var ui = typeof(KSP.UI.UIMasterController);
                harmony.Patch(AccessTools.Method(ui, nameof(KSP.UI.UIMasterController.ShowUI)),
                              prefix: new HarmonyMethod(typeof(HarmonyHook), nameof(ShowUIPrefix)));
                var onShow = AccessTools.Method(ui, "OnShowUI");
                if (onShow != null)
                    harmony.Patch(onShow, prefix: new HarmonyMethod(typeof(HarmonyHook), nameof(ShowUIPrefix)));
            }
            catch (Exception e) { Log.Warn("HUD hold not installed: " + e.Message); }
            // Mods' own HUDs follow the onShowUI / onHideUI events: the show event is held back too.
            try
            {
                harmony.Patch(AccessTools.Method(typeof(EventVoid), nameof(EventVoid.Fire)),
                              prefix: new HarmonyMethod(typeof(HarmonyHook), nameof(EventFirePrefix)));
            }
            catch (Exception e) { Log.Warn("HUD event hold not installed: " + e.Message); }

            // KSPTextureLoader refuses blocking loads while a scene switch is pending (it would wait on
            // Unity's async queue). The queue is stalled the whole time a preloaded scene is held, so
            // it is told the switch is pending then too: its loads go async instead of hanging the game.
            try
            {
                Type tl = AccessTools.TypeByName("KSPTextureLoader.TextureLoader");
                var getter = tl != null ? AccessTools.PropertyGetter(tl, "PendingSceneSwitch") : null;
                if (getter != null)
                    harmony.Patch(getter, postfix: new HarmonyMethod(typeof(HarmonyHook), nameof(PendingSceneSwitchPostfix)));
            }
            catch (Exception e) { Log.Warn("KSPTextureLoader guard not installed: " + e.Message); }

            // EVE's cloud update has a Space Center branch that always places the 2D cloud layer as
            // the close-up (local) layer. The Space Center camera never leaves the ground, but our
            // planet shot switches the terrain off, EVE moves the layer to scaled space, and that
            // branch then put it in the wrong place: no clouds on the planet shot or in the zoom.
            // While Seamless has the terrain off there, EVE's update runs its flight branch (the
            // scene reads as flight for that one call), which places the scaled layer correctly.
            try
            {
                Type cp = AccessTools.TypeByName("Atmosphere.CloudsPQS");
                var upd = cp != null ? AccessTools.Method(cp, "Update") : null;
                if (upd != null)
                {
                    harmony.Patch(upd, prefix: new HarmonyMethod(typeof(HarmonyHook), nameof(CloudsUpdatePrefix)),
                                  finalizer: new HarmonyMethod(typeof(HarmonyHook), nameof(CloudsUpdateFinalizer)));
                    Log.Info("EVE clouds: scaled layer placement at the Space Center patched");
                }
            }
            catch (Exception e) { Log.Warn("EVE clouds patch not installed: " + e.Message); }
        }

        private static void CloudsUpdatePrefix(out GameScenes __state)
        {
            __state = HighLogic.LoadedScene;
            try { if (__state == GameScenes.SPACECENTER && Transitions.SpaceCenterTerrainOff) HighLogic.LoadedScene = GameScenes.FLIGHT; }
            catch { }
        }

        private static Exception CloudsUpdateFinalizer(Exception __exception, GameScenes __state)
        {
            HighLogic.LoadedScene = __state;
            return __exception;
        }

        private static bool LoadScenePrefix(GameScenes scene)
        {
            return Transitions.AllowLoadScene(scene);
        }

        private static bool BufferedLoadPrefix(int sceneToBeLoaded, ref IEnumerator __result)
        {
            IEnumerator mine;
            try { mine = ScenePreload.TakeOver(sceneToBeLoaded); }
            catch (Exception e) { Log.Warn("preloaded scene not used: " + e); mine = null; }
            if (mine == null) return true;
            __result = mine;
            return false;
        }

        private static IEnumerable<CodeInstruction> LoadSceneTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return instructions.MethodReplacer(
                AccessTools.Method(typeof(SceneManager), nameof(SceneManager.LoadScene), new[] { typeof(int) }),
                AccessTools.Method(typeof(ScenePreload), nameof(ScenePreload.LoadScene)));
        }

        private static bool EventFirePrefix(EventVoid __instance)
        {
            if (!ReferenceEquals(__instance, GameEvents.onShowUI)) return true;
            try { return !Transitions.HoldingHud; }
            catch { return true; }
        }

        private static bool ShowUIPrefix()
        {
            try { return !Transitions.HoldingHud; }
            catch { return true; }
        }

        private static void PendingSceneSwitchPostfix(ref bool __result)
        {
            if (!__result && ScenePreload.Stalling)
            {
                __result = true;
                TextureLoaderDeferrals++;
            }
        }
    }
}
