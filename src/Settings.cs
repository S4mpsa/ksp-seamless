using System;
using System.IO;
using UnityEngine;

namespace Seamless
{
    /// <summary>GameData/Seamless/settings.cfg, read once per flight scene.</summary>
    internal sealed class Settings
    {
        public bool enabled = true;

        // trigger
        public float quietSeconds = 1f;      // engines off for this long before the shot starts
        public bool includeStars = false;    // also play when a vessel is "captured" by a star

        // framing (fractions of the screen)
        public float planetFraction = 0.9f;  // planet diameter / height between the bars (0.9 = 0.68 screen heights)
        public float planetScreenX = 0.3f;   // planet centre, from the left edge
        public float barFraction = 0.12f;    // each letterbox bar, of the screen height
        public float cameraFov = 50f;        // vertical FOV when the camera can pull back far enough
        public float minFov = 0.5f;          // narrowest telephoto FOV used for far-away planets
        public float vesselScreenX = 0.64f;  // where the vessel goes when the camera stays close to it (clear of the limb)
        public float vesselScreenY = 0.34f;
        public float vesselFraction = 0.10f; // vessel size on screen in that case
        public float maxSunAngle = 50f;      // far shots: camera at most this far from the planet's lit side
        public bool sunFromTextSide = true;  // far shots: sun beside the camera on the text side
        public float sunSideAngle = 53f;     // ... this many degrees from the camera (53: a fifth of the disc dark)
        public float maxZoomOut = 4f;
        public float starShotZoomOut = 15f;  // hidden: a craft orbiting a star: the card shot this many times further out than the vessel-size distance        // far shots may pull back this many times further to show a high vessel
        public float pushIn = 0.04f;         // slow zoom while holding (fraction of the FOV)
        public float skyboxBrightness = 0.35f; // skybox brightness during the shot (1 = unchanged)
        public float skyboxMinFov = 25f;     // the skybox is never zoomed in further than this

        // timing (real seconds)
        public float approachSeconds = 5f;
        public float holdSeconds = 7f;
        public float returnSeconds = 3.5f;

        // text
        public bool showApPe = true;
        public bool showMissionTime = true;
        public bool showVesselMarker = true;

        // loading-screen transitions (need Harmony)
        public bool spaceCenterTransition = true;
        public bool trackingStationTransition = true;
        public bool vesselSwitchTransition = true;
        public bool flyFromSpaceCenter = true;     // Space Center -> a craft (markers, launch pad, runway)
        public bool flyFromTrackingStation = true; // Tracking Station "Fly"
        public bool editorTransition = true;       // Space Center -> VAB / SPH
        public bool editorExitTransition = true;   // VAB / SPH -> Space Center
        public bool trackingFromSpaceCenter = true;// Space Center -> Tracking Station
        public bool launchTransition = true;       // VAB / SPH -> launch site
        public float launchDoorSeconds = 1f;     // hidden: launch from the VAB / SPH: the camera turns to look out of the doors over this long (0 = off)
        public float vabDoorHeading = float.NaN; // hidden: the VAB doors' direction (deg, editor scene, 0 = +Z, 90 = +X) when the scan gets it wrong
        public float sphDoorHeading = float.NaN; // hidden: the same for the SPH
        public float launchHoldSeconds = 0.5f;     // bars + card over the editor before the load
        public bool spaceCenterFromTrackingStation = true; // Tracking Station -> Space Center
        public bool saveLoadTransition = true;     // main menu -> a save's Space Center
        public bool mainMenuTransition = true;     // quitting to the main menu
        public float saveHoldSeconds = 0.5f;       // the Kerbin shot holds this long before the zoom down
        public float settleQuietSeconds = 0.5f;      // Space Center arrivals: the scene counts as settled after this long with no log output
        public float settleMaxSeconds = 30f;       // ... or after this long regardless
        public float warmUpSeconds = 0.5f;           // ... first the camera waits at the Space Center view under the card (terrain, clouds built); 0 = off
        public bool liveLoading = true;            // show the new scene while KSP still finishes loading it
        public bool preloadScene = true;           // read the new scene in the background during the departure
        public bool inSceneSwitch = true;          // switching to a craft in space: load it and switch in place, no scene reload
        public bool dynamicTiming = true;          // preloaded transitions: timing from recorded load times
        public float cardTargetSeconds = 5f;       // ... flights lengthened (never sped up) so the card shows about this long
        public float cardSkipSeconds = 1.5f;       // ... no card when the load is expected to be over this soon (0 = always a card)
        public float dynamicMaxSeconds = 30f;      // ... flights are lengthened to at most this long
        public bool keepShotAngle = true;          // a switch's zoom ends looking at the craft from the opening shot's side
        public float departTurnSeconds = 1f; // the opening turn towards the destination, in seconds (any trip length)
        public float departSeconds = 1.5f;      // shortest flight to the destination
        public float departMaxSeconds = 6f;  // longest flight (interplanetary)
        public float flightMaxRate = 5f;      // flights: fastest view change, e-folds of distance (plus radians of turn) per second
        public float maxTurnRate = 90f;      // hidden: the transition camera never turns faster than this (deg/s; 0 = off)
        public float maxTurnAccel = 180f;    // hidden: ... nor speeds its turn up or down faster than this (deg/s^2)
        public float flightMaxAccel = 6f;   // ... and its largest change per second (gentle starts and ends)
        public float loadingMinSeconds = 0f;  // the loading card shows at least this long
        public float establishSeconds = 0f;   // vessel switch: hold on the opening shot
        public float zoomInSeconds = 2.5f;      // vessel switch: zoom into the normal camera view

        // keys
        public KeyCode skipKey = KeyCode.Space;
        public KeyCode previewKey = KeyCode.KeypadMultiply;

        public bool debugLog = false;
        public float transitionTimeScale = 1f;   // every leg of the transitions takes this many times as long (1 = as designed, 1.5 = slower)
        public bool showPlanetFacts = true;   // hidden: radius, gravity, atmosphere, day, moons on the arrival card
        public bool frameProbe = false;   // hidden: one log line per rendered frame of every camera move

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                // Read the file itself (edits apply on the next flight scene, no restart needed);
                // the game database copy (loaded at startup) is the fallback.
                ConfigNode n = null;
                string path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/Seamless/settings.cfg");
                if (File.Exists(path))
                {
                    ConfigNode root = ConfigNode.Load(path);
                    if (root != null) n = root.GetNode("SEAMLESS");
                }
                if (n == null && GameDatabase.Instance != null)
                {
                    ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("SEAMLESS");
                    if (nodes != null && nodes.Length > 0) n = nodes[0];
                }
                if (n == null)
                {
                    Log.Info("no SEAMLESS settings found - using defaults");
                    return s;
                }

                Bool(n, "enabled", ref s.enabled);
                Float(n, "quietSeconds", ref s.quietSeconds);
                Bool(n, "includeStars", ref s.includeStars);
                Float(n, "planetFraction", ref s.planetFraction);
                Float(n, "planetScreenX", ref s.planetScreenX);
                Float(n, "barFraction", ref s.barFraction);
                Float(n, "cameraFov", ref s.cameraFov);
                Float(n, "minFov", ref s.minFov);
                Float(n, "vesselScreenX", ref s.vesselScreenX);
                Float(n, "vesselScreenY", ref s.vesselScreenY);
                Float(n, "vesselFraction", ref s.vesselFraction);
                Float(n, "maxSunAngle", ref s.maxSunAngle);
                Bool(n, "sunFromTextSide", ref s.sunFromTextSide);
                Float(n, "sunSideAngle", ref s.sunSideAngle);
                Float(n, "maxZoomOut", ref s.maxZoomOut);
                Float(n, "starShotZoomOut", ref s.starShotZoomOut);
                Float(n, "pushIn", ref s.pushIn);
                Float(n, "skyboxBrightness", ref s.skyboxBrightness);
                Float(n, "skyboxMinFov", ref s.skyboxMinFov);
                Float(n, "approachSeconds", ref s.approachSeconds);
                Float(n, "holdSeconds", ref s.holdSeconds);
                Float(n, "returnSeconds", ref s.returnSeconds);
                Bool(n, "showApPe", ref s.showApPe);
                Bool(n, "showMissionTime", ref s.showMissionTime);
                Bool(n, "showVesselMarker", ref s.showVesselMarker);
                Bool(n, "spaceCenterTransition", ref s.spaceCenterTransition);
                Bool(n, "trackingStationTransition", ref s.trackingStationTransition);
                Bool(n, "vesselSwitchTransition", ref s.vesselSwitchTransition);
                Bool(n, "flyFromSpaceCenter", ref s.flyFromSpaceCenter);
                Bool(n, "flyFromTrackingStation", ref s.flyFromTrackingStation);
                Bool(n, "editorTransition", ref s.editorTransition);
                Bool(n, "editorExitTransition", ref s.editorExitTransition);
                Bool(n, "trackingFromSpaceCenter", ref s.trackingFromSpaceCenter);
                Bool(n, "launchTransition", ref s.launchTransition);
                Float(n, "launchHoldSeconds", ref s.launchHoldSeconds);
                Float(n, "launchDoorSeconds", ref s.launchDoorSeconds);
                Float(n, "vabDoorHeading", ref s.vabDoorHeading);
                Float(n, "sphDoorHeading", ref s.sphDoorHeading);
                Bool(n, "spaceCenterFromTrackingStation", ref s.spaceCenterFromTrackingStation);
                Bool(n, "saveLoadTransition", ref s.saveLoadTransition);
                Bool(n, "mainMenuTransition", ref s.mainMenuTransition);
                Float(n, "saveHoldSeconds", ref s.saveHoldSeconds);
                Float(n, "settleQuietSeconds", ref s.settleQuietSeconds);
                Float(n, "settleMaxSeconds", ref s.settleMaxSeconds);
                Float(n, "warmUpSeconds", ref s.warmUpSeconds);
                Bool(n, "liveLoading", ref s.liveLoading);
                Bool(n, "preloadScene", ref s.preloadScene);
                Bool(n, "inSceneSwitch", ref s.inSceneSwitch);
                Bool(n, "dynamicTiming", ref s.dynamicTiming);
                Float(n, "cardTargetSeconds", ref s.cardTargetSeconds);
                Float(n, "cardSkipSeconds", ref s.cardSkipSeconds);
                Float(n, "dynamicMaxSeconds", ref s.dynamicMaxSeconds);
                Bool(n, "keepShotAngle", ref s.keepShotAngle);
                Float(n, "departTurnSeconds", ref s.departTurnSeconds);
                Float(n, "departSeconds", ref s.departSeconds);
                Float(n, "departMaxSeconds", ref s.departMaxSeconds);
                Float(n, "flightMaxRate", ref s.flightMaxRate);
                Float(n, "flightMaxAccel", ref s.flightMaxAccel);
                Float(n, "maxTurnRate", ref s.maxTurnRate);
                Float(n, "maxTurnAccel", ref s.maxTurnAccel);
                Float(n, "loadingMinSeconds", ref s.loadingMinSeconds);
                Float(n, "establishSeconds", ref s.establishSeconds);
                Float(n, "zoomInSeconds", ref s.zoomInSeconds);
                Key(n, "skipKey", ref s.skipKey);
                Key(n, "previewKey", ref s.previewKey);
                Bool(n, "debugLog", ref s.debugLog);
                Float(n, "transitionTimeScale", ref s.transitionTimeScale);
                Bool(n, "showPlanetFacts", ref s.showPlanetFacts);
                Bool(n, "frameProbe", ref s.frameProbe);
            }
            catch (Exception e)
            {
                Log.Warn("settings.cfg unreadable, using defaults: " + e.Message);
            }
            s.Clamp();
            return s;
        }

        private void Clamp()
        {
            quietSeconds = Mathf.Max(0f, quietSeconds);
            planetFraction = Mathf.Clamp(planetFraction, 0.05f, 1.5f);
            planetScreenX = Mathf.Clamp(planetScreenX, 0.05f, 0.95f);
            barFraction = Mathf.Clamp(barFraction, 0f, 0.3f);
            cameraFov = Mathf.Clamp(cameraFov, 5f, 120f);
            minFov = Mathf.Clamp(minFov, 0.05f, cameraFov);
            vesselScreenX = Mathf.Clamp01(vesselScreenX);
            vesselScreenY = Mathf.Clamp01(vesselScreenY);
            vesselFraction = Mathf.Clamp(vesselFraction, 0.01f, 0.8f);
            maxSunAngle = Mathf.Clamp(maxSunAngle, 0f, 180f);
            sunSideAngle = Mathf.Clamp(sunSideAngle, 0f, 90f);
            maxZoomOut = Mathf.Clamp(maxZoomOut, 1f, 20f);
            transitionTimeScale = Mathf.Clamp(transitionTimeScale, 0.25f, 4f);
            starShotZoomOut = Mathf.Clamp(starShotZoomOut, 1f, 200f);
            pushIn = Mathf.Clamp(pushIn, 0f, 0.5f);
            skyboxBrightness = Mathf.Clamp01(skyboxBrightness);
            skyboxMinFov = Mathf.Clamp(skyboxMinFov, 0f, 120f);
            approachSeconds = Mathf.Max(0.5f, approachSeconds);
            holdSeconds = Mathf.Max(0f, holdSeconds);
            returnSeconds = Mathf.Max(0.5f, returnSeconds);
            departTurnSeconds = Mathf.Max(0.3f, departTurnSeconds);
            launchHoldSeconds = Mathf.Max(0.3f, launchHoldSeconds);
            launchDoorSeconds = Mathf.Clamp(launchDoorSeconds, 0f, 5f);
            saveHoldSeconds = Mathf.Max(0f, saveHoldSeconds);
            settleQuietSeconds = Mathf.Clamp(settleQuietSeconds, 0f, 10f);
            settleMaxSeconds = Mathf.Clamp(settleMaxSeconds, 0f, 300f);
            warmUpSeconds = Mathf.Clamp(warmUpSeconds, 0f, 60f);
            departSeconds = Mathf.Max(0.5f, departSeconds);
            departMaxSeconds = Mathf.Max(departSeconds, departMaxSeconds);
            flightMaxRate = Mathf.Clamp(flightMaxRate, 0.2f, 20f);
            flightMaxAccel = Mathf.Clamp(flightMaxAccel, 0.1f, 20f);
            maxTurnRate = maxTurnRate <= 0f ? 0f : Mathf.Clamp(maxTurnRate, 10f, 720f);
            maxTurnAccel = Mathf.Clamp(maxTurnAccel, 10f, 2000f);
            loadingMinSeconds = Mathf.Max(0f, loadingMinSeconds);
            cardTargetSeconds = Mathf.Clamp(cardTargetSeconds, 0f, 60f);
            cardSkipSeconds = Mathf.Clamp(cardSkipSeconds, 0f, 10f);
            dynamicMaxSeconds = Mathf.Clamp(dynamicMaxSeconds, 0f, 120f);
            establishSeconds = Mathf.Max(0f, establishSeconds);
            zoomInSeconds = Mathf.Max(0.5f, zoomInSeconds);
        }

        private static void Bool(ConfigNode n, string k, ref bool v)
        {
            string s = n.GetValue(k);
            if (s != null && bool.TryParse(s.Trim(), out bool r)) v = r;
        }

        private static void Float(ConfigNode n, string k, ref float v)
        {
            string s = n.GetValue(k);
            if (s != null && float.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float r)) v = r;
        }

        private static void Key(ConfigNode n, string k, ref KeyCode v)
        {
            string s = n.GetValue(k);
            if (string.IsNullOrEmpty(s)) return;
            try { v = (KeyCode)Enum.Parse(typeof(KeyCode), s.Trim(), true); }
            catch { Log.Warn("settings.cfg: unknown key '" + s + "' for " + k); }
        }
    }

    internal static class Log
    {
        public static void Info(string m) { Debug.Log("[Seamless] " + m); }
        public static void Warn(string m) { Debug.LogWarning("[Seamless] " + m); }
    }
}
