using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Seamless
{
    /// <summary>
    /// Loads the next scene in the background while the departure plays in the old one (setting
    /// <c>preloadScene</c>). KSP's own buffered load (<c>HighLogic.bufferedLoad</c>) only starts
    /// reading the new scene after it has thrown the old one away through its "loadingBuffer"
    /// scene. Here the new scene is read with <c>LoadSceneAsync(Single)</c> as soon as the
    /// transition starts, held before activation (Unity stops it at 90 %), and when the departure
    /// ends a Harmony prefix on <c>bufferedLoad</c> runs <see cref="Finish"/> in its place: the old
    /// scene's objects are destroyed (as the loadingBuffer scene would), then the new scene is
    /// activated, then the old scene's assets are unloaded. A game that loads the scene straight
    /// from <c>HighLogic.LoadScene</c> (Retooled) never calls <c>bufferedLoad</c>, so that
    /// <c>SceneManager.LoadScene</c> call is replaced with <see cref="LoadScene"/>, which does the same.
    ///
    /// Costs: while held, Unity's whole async-load queue waits behind it (asset bundle / async
    /// resource loads requested during the departure finish only after the switch), and the old
    /// scene's assets are unloaded after the new scene is up instead of before (a higher memory
    /// peak during the switch). Only for KSP's buffered (async) transitions.
    /// </summary>
    internal static class ScenePreload
    {
        private static AsyncOperation op;
        private static int index = -1;
        private static List<Scene> oldScenes;
        private static float startedAt, readyAt;
        private static int idleFrames;
        private static bool finishing;

        /// <summary>A preloaded scene is waiting for activation (the async-load queue is stalled).</summary>
        internal static bool Held => op != null;

        /// <summary>Held and not yet told to activate: Unity's async-load queue waits behind it. Once
        /// the switch has started (activation allowed) it is KSP's normal load again, and blocking
        /// texture loads are fine (KSP's own buffered load allows them then too).</summary>
        internal static bool Stalling => op != null && !op.allowSceneActivation;

        internal static GameScenes Target => (GameScenes)index;

        internal static bool Start(GameScenes scene)
        {
            if (op != null || !HarmonyHook.PreloadReady) return false;
            try
            {
                if (HighLogic.fetch == null || HighLogic.fetch.sceneBufferTransitionMatrix == null
                    || !HighLogic.fetch.sceneBufferTransitionMatrix.GetTransitionValue(HighLogic.LoadedScene, scene))
                    return false;   // KSP loads this one synchronously: nothing to overlap
                var old = new List<Scene>();
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene s = SceneManager.GetSceneAt(i);
                    if (s.IsValid() && s.isLoaded) old.Add(s);
                }
                AsyncOperation o = SceneManager.LoadSceneAsync((int)scene, LoadSceneMode.Single);
                if (o == null) return false;
                o.allowSceneActivation = false;
                op = o;
                oldScenes = old;
                index = (int)scene;
                startedAt = Time.realtimeSinceStartup;
                readyAt = -1f;
                idleFrames = 0;
                finishing = false;
                HarmonyHook.TextureLoaderDeferrals = 0;
                Application.backgroundLoadingPriority = ThreadPriority.Low;   // keep the departure smooth
                Log.Info("preloading the " + scene + " scene behind the departure");
                return true;
            }
            catch (Exception e)
            {
                Log.Warn("scene preload not started: " + e.Message);
                return false;
            }
        }

        /// <summary>Per frame while a transition runs: logs when the preload is ready; a preload
        /// left behind by a transition that ended without loading is finished.</summary>
        internal static void Poll(bool transitionIdle)
        {
            if (op == null) return;
            if (readyAt < 0f && op.progress >= 0.9f)
            {
                readyAt = Time.realtimeSinceStartup;
                Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "preloaded scene ready after {0:F1} s", readyAt - startedAt));
            }
            idleFrames = transitionIdle && !finishing ? idleFrames + 1 : 0;
            if (idleFrames == 120)
            {
                // cannot happen (every departure ends in the load); a held load would stall every
                // async load of the game, so go where it was going
                Log.Warn("a preloaded scene was left waiting - loading " + Target);
                Transitions.LoadDirect(Target);
            }
        }

        /// <summary>For the game's scene load (the HighLogic.bufferedLoad prefix, <see cref="LoadScene"/>):
        /// the replacement coroutine, or null to run KSP's own.</summary>
        internal static IEnumerator TakeOver(int sceneToBeLoaded)
        {
            if (op == null) return null;
            if (sceneToBeLoaded != index)
            {
                // KSP's own load would queue behind the held one and never start: finish ours first
                Log.Warn("scene " + (GameScenes)sceneToBeLoaded + " requested while " + Target + " was preloaded");
                return Chain(Finish(), sceneToBeLoaded);
            }
            return Finish();
        }

        /// <summary>Stands in for the SceneManager.LoadScene call in HighLogic.LoadScene, where the
        /// game loads a scene without bufferedLoad: a held preload is finished, as the bufferedLoad
        /// prefix would (the game's own load would queue behind it and never start).</summary>
        internal static void LoadScene(int sceneToBeLoaded)
        {
            IEnumerator mine;
            try { mine = TakeOver(sceneToBeLoaded); }
            catch (Exception e) { Log.Warn("preloaded scene not used: " + e); mine = null; }
            if (mine != null) HighLogic.fetch.StartCoroutine(mine);
            else SceneManager.LoadScene(sceneToBeLoaded);
        }

        private static IEnumerator Chain(IEnumerator first, int then)
        {
            while (first.MoveNext()) yield return first.Current;
            IEnumerator stock = HighLogic.bufferedLoad(then, true);
            while (stock.MoveNext()) yield return stock.Current;
        }

        private static IEnumerator Finish()
        {
            AsyncOperation o = op;
            finishing = true;
            float t0 = Time.realtimeSinceStartup;
            Application.backgroundLoadingPriority = ThreadPriority.High;
            int roots = DestroyOld();   // at the end of this frame, as the loadingBuffer scene would
            Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "switching to the preloaded scene ({0}, {1} old scene objects released{2})",
                o.progress >= 0.9f ? "ready" : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F0} % read", o.progress / 0.009f),
                roots,
                HarmonyHook.TextureLoaderDeferrals > 0 ? ", KSPTextureLoader kept async " + HarmonyHook.TextureLoaderDeferrals + " times" : ""));
            yield return null;
            GC.Collect();
            o.allowSceneActivation = true;
            while (!o.isDone) yield return null;
            op = null;
            index = -1;
            oldScenes = null;
            finishing = false;
            Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "preloaded scene active {0:F1} s after the switch, {1:F1} s after the preload started",
                Time.realtimeSinceStartup - t0, Time.realtimeSinceStartup - startedAt));
            Resources.UnloadUnusedAssets();   // the old scene's assets: after the switch (the price of the preload)
            GC.Collect();
            Application.backgroundLoadingPriority = ThreadPriority.Normal;
        }

        private static int DestroyOld()
        {
            int n = 0;
            if (oldScenes == null) return 0;
            foreach (Scene s in oldScenes)
            {
                if (!s.IsValid() || !s.isLoaded) continue;
                foreach (GameObject go in s.GetRootGameObjects())
                {
                    if (go == null) continue;
                    UnityEngine.Object.Destroy(go);
                    n++;
                }
            }
            return n;
        }
    }
}
