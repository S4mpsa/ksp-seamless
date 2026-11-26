using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using KSP.UI;
using UnityEngine;

namespace Seamless
{
    /// <summary>
    /// Loading screens as camera moves. A Harmony prefix on HighLogic.LoadScene holds back two
    /// scene loads from the flight scene:
    ///
    ///   Space Center: the camera pulls out to the Kerbin shot; that frame is captured and frozen
    ///     on a persistent overlay (bars, "Kerbin / Kerbal Space Center / date"), the load runs
    ///     underneath, and once the Space Center is ready the frozen frame dissolves into it.
    ///   Switching to an unloaded vessel (a full flight-scene reload): the camera pulls out to the
    ///     establishing shot of the target (same planet: the target's own shot; elsewhere: the
    ///     current planet), frozen while loading with the target's name and situation; in the new
    ///     scene the camera starts on that same shot live, dissolves in, and zooms straight into
    ///     the normal flight camera view of the craft (the craft held on screen all the way).
    ///
    /// The 3D world cannot keep rendering through a load (KSP unloads the old scene first), so the
    /// frozen frame is the loading screen. Everything fails safe: an exception or a missing piece
    /// runs the normal load at once.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class Transitions : MonoBehaviour
    {
        private const string LockId = "SeamlessTransition";
        private static readonly FieldInfo MaskInstanceField =
            typeof(LoadingBufferMask).GetField("Instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        private static Transitions instance;

        /// <summary>A transition has hidden the HUD and is still running: KSP's ShowUI is held back
        /// (Cleanup shows it once the transition is over).</summary>
        internal static bool HoldingHud => instance != null && instance.phase != Phase.Idle && instance.uiHidden;
        private static bool bypass;

        private enum Phase { Idle, Depart, Capture, PendingLoad, Loading, Arrive, ArriveKsc, Reveal }
        // SpaceCenter: flight -> Space Center / Tracking Station (reveal); VesselSwitch: to a craft
        // (arrive); Editor: Space Center -> VAB / SPH (reveal); KscToTracking: Space Center ->
        // Tracking Station (reveal); Launch: VAB / SPH -> launch site (arrive from the door);
        // ToKsc: Tracking Station -> Space Center and SaveLoad: main menu -> a save's Space Center
        // (both arrive from a Kerbin shot, zooming down into the Space Center view)
        private enum Kind { SpaceCenter, VesselSwitch, Editor, KscToTracking, Launch, ToKsc, SaveLoad, EditorExit }

        private Settings S;
        private Phase phase = Phase.Idle;
        private Kind kind;
        private GameScenes targetScene;
        private double t;
        private bool loadIssued, sceneArrived, uiHidden;
        private int settle;

        private Vessel switchTarget;
        private int switchFrame = -1;

        // orbit lines of the scene we fly into (hidden while the card is up, restored on cleanup)
        private OrbitLines sceneOrbits;

        // arrive (Space Center): from a Kerbin shot down into the Space Center camera's own view.
        // Everything is re-read every frame (game time runs: the planet turns under the shot).
        private CameraRig kscRig;
        private SpaceCenterCamera2 kscScc;
        private Transform kscCt;               // the Space Center camera's transform (hand-over pose)
        private double kscHomeTan, kscSx;
        private Vector3d kscOffB;              // the opening shot, in Kerbin's own frame
        private Quaternion kscRotB;
        private double kscTan;
        private static double kscLat = double.NaN, kscLon;

        // Live loading: the arrival / reveal starts as soon as the new scene can be shown, while KSP
        // still finishes loading behind it (its own loading screen would stay up for that part).
        private float clickAt, loadAt;         // Time.realtimeSinceStartup
        private bool measuring, stockEndPending;
        private int usableFrames, smooth;      // frames usable / consecutive frames under 0.1 s
        private int stallFrames;
        private float maxStall;
        private double liveAt = -1;            // arrive: t when the frozen frame starts to dissolve
        private bool waitUnpack, unpackLogged;
        private bool retakeRequested;
        private int retakes;

        // KSP's loading screen: drawn invisible while ours covers the scene (its own timing, which
        // also brings the UI back, keeps running), restored as soon as KSP itself hides it.
        private static Camera maskCam;
        private static int maskCull;
        private static CameraClearFlags maskClear;
        private static bool maskSuppressed;

        // depart
        private CameraRig rig;                 // arrival (flight scene)
        private ICamDriver drv;                // departure camera (flight, Space Center or Tracking Station)
        private Func<Vector3d> departAnchor;   // the departure's reference point (craft, or a fixed spot)
        private Func<bool> departAlive;
        private bool departFromFlight;
        private Behaviour reenableOnFail;      // the Space Center camera controller, paused while we fly
        private Vessel departVessel;
        private CelestialBody departBody;
        private CamPose departStart;
        private Func<Vector3d, CamPose> departTarget;   // final shot, relative to the departing anchor
        private CelestialBody travelBody;              // the flight closes in on this planet's centre
        private Func<Vector3d> travelSubject;          // and keeps this point locked on screen
        private Func<Vector3d> travelCenter;           // centre of the zoom when not the planet's (a building)
        private bool noRise;                           // short hops near the ground (into a building)
        private bool riseRequired;                     // the flight needs the turn (rise) to clear the planet
        private bool staticDepart;                     // no camera move before the load (editor)
        private EditorFacility launchFacility;
        private CamPose departMid;                     // end of the turn: behind the craft, facing the destination
        private double turnSeconds, travelSeconds;
        private double safeRadius;                     // departure planet radius + terrain margin
        private Vessel switchVessel;                   // the (unloaded) target during the flight
        private double switchVesselSize;

        // the card as it was in the captured frame (the capture includes it, so the live card
        // continues exactly from there)
        private OverlayFrame cardAtFreeze;
        private CelestialBody framedBody;      // planet in the frozen frame (leader-line layout), or null
        private Vector3d lastCam;
        private Quaternion lastRot;
        private double lastTanV;

        // frozen frame / text
        private Overlay overlay;
        private bool lowerThird;
        private float frozenZoom = 1f;
        private Vector2 frozenPlanet;
        private float frozenPlanetRadius;
        private bool frozenPlanetOnScreen;
        private string titleText, detailText;
        private Guid targetId;

        // arrive (vessel switch)
        private Vessel arriveVessel;
        private Func<Vector3d, CamPose> estPose;
        private double arriveSize;
        private double retStart = -1, retDur;
        private CamPose retFrom;
        private Paths.Plan arrivePlan, kscPlan;
        private double kscSettledAt = -1;
        private double kscWarmDoneAt = -1;     // the camera waited at the Space Center view (under the card) until then

        // the time of the last log line not from us: a scene still loading clouds, terrain and
        // textures logs as it goes (EVE, Parallax, Kopernicus, PQS); quiet = settled
        // (a mod logging every frame must not hold it forever: "quiet" = fewer than 6 lines in the window)
        private static readonly float[] foreignLogs = new float[6];
        private static int foreignLogNext;

        private static void OnLog(string message, string stack, LogType type)
        {
            if (message != null && (message.StartsWith("[Seamless]", StringComparison.Ordinal)
                                    || message.StartsWith("[UIMasterController]", StringComparison.Ordinal))) return;
            foreignLogs[foreignLogNext] = Time.realtimeSinceStartup;
            foreignLogNext = (foreignLogNext + 1) % foreignLogs.Length;
        }

        private bool Settled()
        {
            // the oldest of the last 6 lines is outside the window
            float oldest = foreignLogs[foreignLogNext];
            return MaskHidden() && smooth >= 30 && Time.realtimeSinceStartup - oldest >= S.settleQuietSeconds;
        }   // the zoom goes around the planet (anchor hidden behind it)
        private double skyWeight;

        // time is paused while the camera flies (the load then starts from the same moment)
        private bool pausedByUs;
        private float timeScaleBefore = 1f;
        private bool sceneHudHidden;           // HUD hidden again once the new scene showed it

        // the opening shot, carried across the load in the target planet's own (rotating) frame,
        // so the new scene starts on exactly the frozen frame instead of a re-solved shot
        private bool carryValid, carryAerial;
        private CelestialBody carryBody;
        private Vector3d carryOff;
        private Quaternion carryRot;
        private Vector3 carryRay;
        private double carryTanV;

        // ------------------------------------------------------------------ setup

        private void Awake()
        {
            if (instance != null) { Destroy(gameObject); return; }
            instance = this;
            DontDestroyOnLoad(gameObject);
            GameEvents.onVesselSwitchingToUnloaded.Add(OnSwitchingToUnloaded);
            GameEvents.onLevelWasLoaded.Add(OnLevelWasLoaded);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
            Application.logMessageReceived += OnLog;

            if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "0Harmony"))
            {
                Log.Info("Harmony not installed - loading-screen transitions off");
                return;
            }
            try
            {
                HarmonyHook.Install();
                Log.Info("loading-screen transitions ready");
            }
            catch (Exception e)
            {
                Log.Warn("could not hook scene loading, transitions off: " + e.Message);
            }
        }

        private void OnDestroy()
        {
            if (instance != this) return;
            GameEvents.onVesselSwitchingToUnloaded.Remove(OnSwitchingToUnloaded);
            GameEvents.onLevelWasLoaded.Remove(OnLevelWasLoaded);
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            Application.logMessageReceived -= OnLog;
        }

        /// <summary>Any scene change: KSP's loading screen is drawn normally again (under our card
        /// when the change is ours). Where the frozen frame is drawn under the UI cameras it would
        /// cover the frame instead, so there it stays off through our own load.</summary>
        private void OnSceneLoadRequested(GameScenes scene)
        {
            RestoreMask();
            if (loadIssued && phase == Phase.Loading && overlay != null && overlay.FrozenUnderUi) SuppressMask();
            if (stockEndPending) { stockEndPending = false; Log.Info("next scene requested before KSP's loading screen ended"); }
        }

        private void OnSwitchingToUnloaded(Vessel from, Vessel to)
        {
            switchTarget = to;
            switchFrame = Time.frameCount;
        }

        private void OnLevelWasLoaded(GameScenes scene)
        {
            if (loadIssued && scene == targetScene) sceneArrived = true;
            if (phase != Phase.Idle) hudSceneFrame = Time.frameCount;
        }

        private int hudSceneFrame = -1;

        /// <summary>The new scene's mods start with their HUDs on (they never saw the onHideUI fired
        /// before the load, e.g. RemoteTech's per-scene core): it is fired again a few times after
        /// the scene loads, for those that set up late too.</summary>
        private void HideModHuds()
        {
            if (hudSceneFrame < 0) return;
            if (!uiHidden || phase == Phase.Idle) { hudSceneFrame = -1; return; }
            int f = Time.frameCount - hudSceneFrame;
            if (f == 1 || f == 5 || f == 30 || f == 120 || f == 300)
            {
                try { GameEvents.onHideUI.Fire(); } catch (Exception e) { Log.Warn("hide HUDs: " + e.Message); }
            }
            if (f > 300) hudSceneFrame = -1;
        }

        /// <summary>Harmony prefix body: false = the original LoadScene is skipped (we run it later).</summary>
        internal static bool AllowLoadScene(GameScenes scene)
        {
            if (bypass) { bypass = false; return true; }
            if (instance == null) return true;
            try { return !instance.TryStart(scene); }
            catch (Exception e) { Log.Warn("transition not started: " + e); return true; }
        }

        private bool TryStart(GameScenes scene)
        {
            if (phase == Phase.Depart || phase == Phase.Capture || phase == Phase.PendingLoad)
                return true;   // a second request while we are leaving: already on its way
            if (phase != Phase.Idle) return false;
            if (scene == GameScenes.FLIGHT && (HighLogic.LoadedScene == GameScenes.SPACECENTER || HighLogic.LoadedScene == GameScenes.TRACKSTATION))
                return FlightDriver.StartupBehaviour == FlightDriver.StartupBehaviours.NEW_FROM_FILE
                    ? TryStartLaunchFromBase() : TryStartFromBase();
            if (HighLogic.LoadedScene == GameScenes.SPACECENTER && (scene == GameScenes.EDITOR || scene == GameScenes.TRACKSTATION))
                return TryStartFromKsc(scene);
            if (HighLogic.LoadedScene == GameScenes.EDITOR && scene == GameScenes.FLIGHT)
                return TryStartLaunch();
            if (HighLogic.LoadedScene == GameScenes.EDITOR && scene == GameScenes.SPACECENTER)
                return TryStartEditorExit();
            if (HighLogic.LoadedScene == GameScenes.TRACKSTATION && scene == GameScenes.SPACECENTER)
                return TryStartTrackingToKsc();
            if (HighLogic.LoadedScene == GameScenes.MAINMENU && scene == GameScenes.SPACECENTER)
                return TryStartSaveLoad();
            if (HighLogic.LoadedScene != GameScenes.FLIGHT) return false;

            Vessel v = FlightGlobals.ActiveVessel;
            FlightCamera fc = FlightCamera.fetch;
            if (v == null || v.mainBody == null || fc == null || fc.GetPivot() == null || ScaledCamera.Instance == null) return false;

            S = Settings.Load();
            if (!S.enabled) return false;
            if (scene == GameScenes.SPACECENTER && S.spaceCenterTransition)
            {
                BeginBase(v, fc, GameScenes.SPACECENTER, "Kerbal Space Center");
                return true;
            }
            if (scene == GameScenes.TRACKSTATION && S.trackingStationTransition)
            {
                BeginBase(v, fc, GameScenes.TRACKSTATION, "Tracking Station");
                return true;
            }
            if (scene == GameScenes.FLIGHT && S.vesselSwitchTransition && switchTarget != null
                && switchFrame == Time.frameCount && switchTarget != v && switchTarget.mainBody != null)
            {
                Vessel target = switchTarget;
                switchTarget = null;
                BeginSwitch(v, fc, target);
                return true;
            }
            return false;
        }

        /// <summary>Space Center (vessel marker, launch pad / runway) or Tracking Station "Fly":
        /// both save and call FlightDriver.StartAndFocusVessel(file, index into FlightGlobals.Vessels).</summary>
        private bool TryStartFromBase()
        {
            S = Settings.Load();
            bool ksc = HighLogic.LoadedScene == GameScenes.SPACECENTER;
            if (!S.enabled || !(ksc ? S.flyFromSpaceCenter : S.flyFromTrackingStation)) return false;
            if (FlightDriver.StartupBehaviour != FlightDriver.StartupBehaviours.RESUME_SAVED_FILE
                && FlightDriver.StartupBehaviour != FlightDriver.StartupBehaviours.RESUME_SAVED_CACHE) return false;
            int idx = FlightDriver.FocusVesselAfterLoad;
            var list = FlightGlobals.Vessels;
            if (list == null || idx < 0 || idx >= list.Count) return false;
            Vessel target = list[idx];
            if (target == null || target.mainBody == null || ScaledCamera.Instance == null) return false;

            CelestialBody home = FlightGlobals.GetHomeBody();
            if (ksc)
            {
                if (FlightCamera.fetch == null) return false;
                PrepareTarget(target);
                Log.Info("Space Center -> " + target.vesselName + " (" + target.mainBody.bodyName + ", " + target.situation + ")");
                BeginFromKscCamera(GameScenes.FLIGHT, Kind.VesselSwitch);
                return true;
            }
            else
            {
                PlanetariumCamera pc = PlanetariumCamera.fetch;
                if (pc == null) return false;
                PrepareTarget(target);
                var r = new PlanetariumRig(S, pc);
                Vector3d a0 = ScaledSpace.ScaledToLocalSpace(pc.transform.position);
                Log.Info("Tracking Station -> " + target.vesselName + " (" + target.mainBody.bodyName + ", " + target.situation + ")");
                BeginCommon(r, () => a0, () => PlanetariumCamera.fetch == pc, NearestBody(a0, home), 10.0, false, GameScenes.FLIGHT, Kind.VesselSwitch);
                return true;
            }
        }

        /// <summary>Space Center departures: the persistent flight camera, nested under
        /// SpaceCenterCamera2 (paused while we fly), driven from its current spot.</summary>
        private void BeginFromKscCamera(GameScenes scene, Kind k)
        {
            FlightCamera fc = FlightCamera.fetch;
            var scc = UnityEngine.Object.FindObjectOfType<SpaceCenterCamera2>();
            var r = new CameraRig(S, fc) { HandBackOnLeave = false };
            Vector3d a0 = (Vector3d)fc.transform.position;
            if (scc != null) { scc.enabled = false; reenableOnFail = scc; }
            CelestialBody home = FlightGlobals.GetHomeBody();
            CelestialBody from = FlightGlobals.currentMainBody != null ? FlightGlobals.currentMainBody : home;
            BeginCommon(r, () => a0, () => FlightCamera.fetch == fc, from, 10.0, false, scene, k);
        }

        /// <summary>Space Center -> VAB / SPH (zoom up to the building's facade) or -> Tracking
        /// Station (into orbit, ending on the exact view the Tracking Station opens with).</summary>
        private bool TryStartFromKsc(GameScenes scene)
        {
            S = Settings.Load();
            if (!S.enabled) return false;
            FlightCamera fc = FlightCamera.fetch;
            if (fc == null || ScaledCamera.Instance == null) return false;
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null) return false;
            switchVessel = null;
            framedBody = null;
            lowerThird = true;

            if (scene == GameScenes.EDITOR)
            {
                if (!S.editorTransition) return false;
                EditorFacility f = EditorDriver.editorFacility;
                Vector3d camPos = (Vector3d)fc.transform.position;
                if (!FacadePose(f, home, camPos, out FacadeShot fs)) return false;
                titleText = FacilityName(fs.Fac, f == EditorFacility.SPH ? "Spaceplane Hangar" : "Vehicle Assembly Building");
                detailText = SafeDate();
                noRise = true;
                travelBody = home;
                // the flight curves around the building's middle (from its far side it goes round,
                // not through it), looking at the facade
                Vector3d mid = fs.Mid, look = fs.Look, end = fs.End;
                Quaternion endRot = fs.Rot;
                double tan0 = fs.Tan;
                travelCenter = () => mid;
                travelSubject = () => look;
                departTarget = anchor => new CamPose(end - anchor, endRot, tan0);
                Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture, "Space Center -> {0} ({1}; {2:F0} m from the camera)",
                    titleText, fs.Describe, (end - camPos).magnitude));
                BeginFromKscCamera(GameScenes.EDITOR, Kind.Editor);
                return true;
            }

            if (!S.trackingFromSpaceCenter) return false;
            PlanetariumCamera pc = PlanetariumCamera.fetch;
            if (pc == null) return false;
            // the Tracking Station opens over the Space Center's side of the planet: it keeps the
            // planetarium camera's last heading, which could be the far side - the flight then
            // swung half way round the planet to unbuilt terrain
            AimTrackingOpening(pc, KscSite(home) - home.position, "the Space Center");
            Vector3d tsCam = TrackingStationOpening(pc, home, out Quaternion tsRot, out double tsTan);
            titleText = "Tracking Station";
            detailText = ArrivalText.BodyName(home) + "\n" + SafeDate();
            noRise = false;
            travelBody = home;
            travelCenter = null;
            travelSubject = () => home.position;
            departTarget = anchor => new CamPose(tsCam - anchor, tsRot, tsTan);
            Log.Info("Space Center -> Tracking Station");
            BeginFromKscCamera(GameScenes.TRACKSTATION, Kind.KscToTracking);
            return true;
        }

        /// <summary>
        /// Where the Tracking Station's view opens: it re-targets the planetarium camera on the
        /// home planet and keeps its last heading, pitch and distance (PlanetariumCamera persists),
        /// pivot = AngleAxis(hdg + InverseRotAngle, up) * AngleAxis(pitch, right), camera
        /// = pivot * back * distance (scaled units), looking at the planet.
        /// </summary>
        /// <summary>Sets the planetarium camera's heading and pitch so the Tracking Station opens
        /// with the camera on a given side of the planet (the inverse of TrackingStationOpening).</summary>
        private static void AimTrackingOpening(PlanetariumCamera pc, Vector3d side, string what)
        {
            try
            {
                if (side.sqrMagnitude < 1.0) return;
                Vector3d d = side.normalized;
                double pitch = Math.Asin(Clamp(d.y, -1.0, 1.0));
                double hdgDeg = Math.Atan2(-d.x, -d.z) * 180.0 / Math.PI - Planetarium.InverseRotAngle;
                float h0 = pc.camHdg, p0 = pc.camPitch;
                pc.camHdg = (float)(hdgDeg * Math.PI / 180.0);
                pc.camPitch = Mathf.Clamp((float)pitch, pc.minPitch, pc.maxPitch);
                Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "the Tracking Station opens over {0}: heading {1:F0} -> {2:F0} deg, pitch {3:F0} -> {4:F0} deg",
                    what, h0 * Mathf.Rad2Deg, pc.camHdg * Mathf.Rad2Deg, p0 * Mathf.Rad2Deg, pc.camPitch * Mathf.Rad2Deg));
            }
            catch (Exception e) { Log.Warn("aiming the Tracking Station: " + e.Message); }
        }

        private static Vector3d TrackingStationOpening(PlanetariumCamera pc, CelestialBody home, out Quaternion rot, out double tanV)
        {
            float inv = ScaledSpace.InverseScaleFactor;
            float minR = (float)home.Radius * inv * 2f;
            float dist = Mathf.Clamp(pc.Distance, Mathf.Max(pc.minDistance, minR), pc.maxDistance);
            Quaternion hdg = Quaternion.AngleAxis(pc.camHdg * Mathf.Rad2Deg + (float)Planetarium.InverseRotAngle, Vector3.up);
            rot = hdg * Quaternion.AngleAxis(pc.camPitch * Mathf.Rad2Deg, Vector3.right);
            Camera c = pc.GetComponent<Camera>();
            float fov = c != null ? c.fieldOfView : 60f;
            tanV = Math.Tan(fov * 0.5 * Math.PI / 180.0);
            return home.position + (Vector3d)(rot * Vector3.back) * ((double)dist * ScaledSpace.ScaleFactor);
        }

        /// <summary>VAB / SPH -> launch site: the bars and the card come in over the editor, the
        /// frame freezes; in the flight scene the camera starts at the building's entrance and
        /// flies to the new craft.</summary>
        private bool TryStartLaunch()
        {
            S = Settings.Load();
            if (!S.enabled || !S.launchTransition) return false;
            if (FlightDriver.StartupBehaviour != FlightDriver.StartupBehaviours.NEW_FROM_FILE) return false;
            CelestialBody home = FlightGlobals.GetHomeBody();
            string name = null;
            try { if (EditorLogic.fetch != null && EditorLogic.fetch.ship != null) name = EditorLogic.fetch.ship.shipName; } catch { }
            if (string.IsNullOrEmpty(name)) name = "New vessel";
            launchFacility = EditorDriver.editorFacility;
            titleText = name;
            detailText = SiteLabel(FlightDriver.LaunchSiteName) + "\n" + SafeDate();
            switchVessel = null;
            framedBody = null;
            lowerThird = true;
            staticDepart = true;
            Log.Info("launching " + name + " from " + launchFacility + " to " + FlightDriver.LaunchSiteName);
            BeginCommon(new NullDriver(), () => Vector3d.zero, () => true, home, 10.0, false, GameScenes.FLIGHT, Kind.Launch);
            return true;
        }

        /// <summary>
        /// A new craft launched from the Space Center or the Tracking Station - Kerbal Construction
        /// Time's build list (FlightDriver.StartWithNewLaunch from those scenes). The same as a launch
        /// from the editor: the card over the current view, then the zoom from the VAB / SPH entrance
        /// (the facility KCT sets in HighLogic.CurrentGame.editorFacility) to the new craft.
        /// </summary>
        private bool TryStartLaunchFromBase()
        {
            S = Settings.Load();
            if (!S.enabled || !S.launchTransition) return false;
            CelestialBody home = FlightGlobals.GetHomeBody();
            string name = CraftName(FlightDriver.newShipToLoadPath);
            if (string.IsNullOrEmpty(name)) name = "New vessel";
            launchFacility = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.editorFacility : EditorFacility.VAB;
            titleText = name;
            detailText = SiteLabel(FlightDriver.LaunchSiteName) + "\n" + SafeDate();
            switchVessel = null;
            framedBody = null;
            lowerThird = true;
            staticDepart = true;
            carryValid = false;
            Log.Info("launching " + name + " (" + launchFacility + ") to " + FlightDriver.LaunchSiteName + " from the "
                     + (HighLogic.LoadedScene == GameScenes.SPACECENTER ? "Space Center" : "Tracking Station"));
            BeginCommon(new NullDriver(), () => Vector3d.zero, () => true, home, 10.0, false, GameScenes.FLIGHT, Kind.Launch);
            return true;
        }

        private static string CraftName(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
                foreach (string line in System.IO.File.ReadLines(path))
                {
                    string l = line.Trim();
                    int eq = l.IndexOf('=');
                    if (eq > 0 && l.Substring(0, eq).Trim() == "ship") return l.Substring(eq + 1).Trim();
                    if (l.StartsWith("PART", StringComparison.Ordinal)) break;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Tracking Station -> Space Center: fly to the Kerbin shot with the Space Center
        /// on the visible side; after the load the camera starts there and zooms down into the
        /// Space Center view.</summary>
        private bool TryStartTrackingToKsc()
        {
            S = Settings.Load();
            if (!S.enabled || !S.spaceCenterFromTrackingStation) return false;
            PlanetariumCamera pc = PlanetariumCamera.fetch;
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (pc == null || home == null || ScaledCamera.Instance == null) return false;
            Vector3d site = KscSite(home);
            Shot shot = ShotSolver.SolveFar(S, site, home, Shots.SunDir(home), CameraRig.Aspect);
            double sx = 2.0 * S.planetScreenX - 1.0;
            Carry(home, false, shot.Offset, shot.Rot, Vector3.forward, shot.TanV);
            titleText = ArrivalText.BodyName(home);
            detailText = "Kerbal Space Center\n" + SafeDate();
            framedBody = home;
            lowerThird = false;
            switchVessel = null;
            travelBody = home;
            travelCenter = null;
            travelSubject = () => home.position;
            departTarget = anchor =>
            {
                CamPose p = Shots.Reaim(shot, site, home, sx, shot.TanV);
                return new CamPose(site + p.Off - anchor, p.Rot, p.TanV);
            };
            var r = new PlanetariumRig(S, pc);
            Vector3d a0 = ScaledSpace.ScaledToLocalSpace(pc.transform.position);
            Log.Info("Tracking Station -> Space Center");
            BeginCommon(r, () => a0, () => PlanetariumCamera.fetch == pc, NearestBody(a0, home), 10.0, false, GameScenes.SPACECENTER, Kind.ToKsc);
            return true;
        }

        /// <summary>Main menu -> a save (resume or new game): the bars and "save / mode / date"
        /// come in over the menu, that frame holds while the game loads, then the Space Center
        /// opens on a Kerbin shot and zooms down into its own view.</summary>
        private bool TryStartSaveLoad()
        {
            S = Settings.Load();
            if (!S.enabled || !S.saveLoadTransition) return false;
            Game g = HighLogic.CurrentGame;
            if (g == null) return false;
            string mode;
            switch (g.Mode)
            {
                case Game.Modes.CAREER: mode = "Career"; break;
                case Game.Modes.SCIENCE_SANDBOX: mode = "Science"; break;
                case Game.Modes.SANDBOX: mode = "Sandbox"; break;
                default: return false;   // missions, scenarios, tutorials
            }
            string name = HighLogic.SaveFolder;
            if (string.IsNullOrEmpty(name)) name = g.Title;
            if (string.IsNullOrEmpty(name)) name = "Kerbal Space Program";
            double ut = 0.0;
            try { if (g.flightState != null) ut = g.flightState.universalTime; } catch { }
            string date;
            try { date = KSPUtil.PrintDate(ut, false); } catch { date = ""; }
            titleText = name;
            detailText = mode + "\n" + date;
            framedBody = null;
            lowerThird = true;
            switchVessel = null;
            staticDepart = true;
            carryValid = false;
            Log.Info("loading save " + name + " (" + mode + ", " + date + ")");
            HideMenu();
            CelestialBody home = null;
            try { home = FlightGlobals.GetHomeBody(); } catch { }
            BeginCommon(new NullDriver(), () => Vector3d.zero, () => true, home, 10.0, false,
                        GameScenes.SPACECENTER, Kind.SaveLoad);
            return true;
        }

        private struct FacadeShot
        {
            public PSystemSetup.SpaceCenterFacility Fac;
            public Vector3d End, Look, Mid;
            public Quaternion Rot;
            public double Tan;
            public string Describe;
        }

        /// <summary>
        /// The view of the VAB / SPH from outside: in front of the side facing its launch site (the
        /// VAB's doors toward the pad, the SPH's toward the runway; the camera's side when that site
        /// is not found), far enough back that the whole building is in frame (its visible height
        /// ~75 % of the view), a little above the ground, looking at the facade's middle. Where the
        /// flight into the building ends, and where leaving it starts.
        /// </summary>
        private bool FacadePose(EditorFacility f, CelestialBody home, Vector3d camPos, out FacadeShot fs)
        {
            fs = new FacadeShot();
            var fac = Facility(f == EditorFacility.SPH ? "SpaceplaneHangar" : "VehicleAssemblyBuilding");
            if (fac == null || fac.facilityTransform == null) return false;
            if (!BuildingBox(fac.facilityTransform, out Vector3d bc, out Vector3 ext, out Quaternion brot, out string boxHow)) return false;
            Vector3d up = (bc - home.position).normalized;
            string sideFrom = f == EditorFacility.SPH ? "the runway" : "the launch pad";
            var site = Facility(f == EditorFacility.SPH ? "Runway" : "LaunchPad");
            Vector3d hc = site != null && site.facilityTransform != null ? (Vector3d)site.facilityTransform.position - bc : camPos - bc;
            if (site == null || site.facilityTransform == null) sideFrom = "the camera";
            hc = hc - up * Vector3d.Dot(hc, up);
            hc = hc.sqrMagnitude > 1.0 ? hc.normalized : ShotSolver.Perp(up);
            double support = Support(ext, brot, hc), halfH = Support(ext, brot, up);
            // heights from the real ground in front of the facade: the building's box reaches below
            // the ground (foundations), so its bottom is no reference
            Vector3d e0 = bc + hc * (support + 6.0);
            double gE = BaseGround(home, e0, bc);
            double vis = Math.Max(10.0, Math.Min((bc - home.position).magnitude + halfH - gE, 2.0 * halfH));
            double back = Clamp(1.35 * vis, 40.0, 400.0);
            Vector3d e1 = bc + hc * (support + back);
            double g1 = Math.Max(GroundRadius(home, e1, true), gE - 5.0);
            fs.End = home.position + (e1 - home.position).normalized * (g1 + Clamp(0.3 * vis, 12.0, 60.0));
            Vector3d face = bc + hc * support;
            fs.Look = home.position + (face - home.position).normalized * (gE + 0.45 * vis);
            fs.Mid = home.position + (bc - home.position).normalized * (gE + 0.45 * vis);
            fs.Tan = Math.Tan(S.cameraFov * 0.5 * Math.PI / 180.0);
            fs.Rot = ShotSolver.AimAnyRay(fs.Look - fs.End, new Vector3d(0, 0, 1), up);
            fs.Fac = fac;
            fs.Describe = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "ground {0:F0} m, building {1:F0} m high, {2:F0} m across, from {3}; view {4:F0} m in front of the side facing {5}, {6:F0} m above the ground, looking at {7:F0} m",
                gE - home.Radius, vis, 2.0 * support, boxHow, back, sideFrom, (fs.End - home.position).magnitude - g1, 0.45 * vis);
            return true;
        }

        private EditorFacility exitFacility;

        /// <summary>VAB / SPH -> Space Center: the card comes in over the editor and that frame holds
        /// while the game loads and settles; then the Space Center opens just outside the building
        /// (the view the flight into it ends on) and the camera flies back to the Space Center's
        /// own view - no trip to orbit.</summary>
        private bool TryStartEditorExit()
        {
            S = Settings.Load();
            if (!S.enabled || !S.editorExitTransition) return false;
            CelestialBody home = null;
            try { home = FlightGlobals.GetHomeBody(); } catch { }
            if (home == null) return false;
            titleText = "Kerbal Space Center";
            detailText = ArrivalText.BodyName(home) + "\n" + SafeDate();
            framedBody = null;
            lowerThird = true;
            switchVessel = null;
            staticDepart = true;
            carryValid = false;
            exitFacility = EditorDriver.editorFacility;
            Log.Info("leaving the " + (exitFacility == EditorFacility.SPH ? "SPH" : "VAB") + " for the Space Center");
            BeginCommon(new NullDriver(), () => Vector3d.zero, () => true, home, 10.0, false,
                        GameScenes.SPACECENTER, Kind.EditorExit);
            return true;
        }

        /// <summary>The Space Center's surface point (remembered from the Space Center scene;
        /// the stock site until then).</summary>
        private static Vector3d KscSite(CelestialBody home)
        {
            RememberKsc();
            bool known = !double.IsNaN(kscLat);
            return home.GetWorldSurfacePosition(known ? kscLat : -0.0972, known ? kscLon : -74.5577, 0.0);
        }

        private static void RememberKsc()
        {
            try
            {
                SpaceCenter sc = SpaceCenter.Instance;
                if (sc != null && (sc.Latitude != 0.0 || sc.Longitude != 0.0)) { kscLat = sc.Latitude; kscLon = sc.Longitude; }
            }
            catch { }
        }

        private static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }

        /// <summary>
        /// Radius (from the planet's centre) of what is below a point: with buildings = a ray down
        /// against the local scenery (terrain and buildings); otherwise, or when the ray finds
        /// nothing, the terrain height of the planet's surface definition; never under the sea.
        /// </summary>
        private static double GroundRadius(CelestialBody b, Vector3d p, bool withBuildings)
        {
            Vector3d rel = p - b.position;
            double r = rel.magnitude;
            double g = -1.0;
            if (r < 1.0) return b.Radius;
            Vector3d up = rel / r;
            double terrain = -1.0;
            try
            {
                // by latitude / longitude: PQS.GetRelativePosition uses the PQS's cached rotation,
                // which read the terrain somewhere else (1030 m under the VAB at the Space Center)
                if (b.pqsController != null)
                    terrain = b.Radius + b.TerrainAltitude(b.GetLatitude(p), b.GetLongitude(p), true);
            }
            catch { }
            if (double.IsNaN(terrain)) terrain = -1.0;
            if (withBuildings && r - b.Radius < 30000.0)
            {
                try
                {
                    if (Physics.Raycast((Vector3)(p + up * 3000.0), (Vector3)(-up), out RaycastHit hit, 6000f, 1 << 15, QueryTriggerInteraction.Ignore))
                        g = ((Vector3d)hit.point - b.position).magnitude;
                }
                catch { }
                // ground slabs and aprons count; a roof, canopy or crane over the spot does not
                if (g > 0.0 && terrain > 0.0 && g > terrain + 25.0) g = terrain;
            }
            if (g < 0.0) g = terrain;
            if (g < 0.0) g = b.Radius;
            if (b.ocean) g = Math.Max(g, b.Radius);
            return g;
        }

        /// <summary>The ground beside a Space Center building: the ground found there, but never
        /// below the terrain under the building itself (a miss or the sea read off its edge).</summary>
        private static double BaseGround(CelestialBody b, Vector3d p, Vector3d building)
        {
            double g = GroundRadius(b, p, true);
            double under = GroundRadius(b, building, false);
            return under > b.Radius + 1.0 && g < under - 15.0 ? under : g;
        }

        /// <summary>
        /// Keeps a camera above the terrain + margin with a soft floor: well above it nothing
        /// changes, near it the camera eases up smoothly (a hard clamp shows as a sudden flat
        /// glide along the ground).
        /// </summary>
        private static Vector3d SoftGround(CelestialBody b, Vector3d cam, double margin, double k)
        {
            if (b == null) return cam;
            Vector3d rel = cam - b.position;
            double r = rel.magnitude;
            if (r < 1.0 || r - b.Radius > 20000.0) return cam;
            double floor = GroundRadius(b, cam, false) + margin;
            double h = r - floor;
            double hs = 0.5 * (h + Math.Sqrt(h * h + k * k));   // smooth max(h, 0)
            if (hs - h < 0.01) return cam;
            return b.position + rel * ((floor + hs) / r);
        }

        private static string SiteLabel(string site)
        {
            if (string.IsNullOrEmpty(site) || site == "LaunchPad") return "Launch Pad";
            if (site == "Runway") return "Runway";
            return site;
        }

        private static PSystemSetup.SpaceCenterFacility Facility(string name)
        {
            try { return PSystemSetup.Instance != null ? PSystemSetup.Instance.GetSpaceCenterFacility(name) : null; }
            catch { return null; }
        }

        private static string FacilityName(PSystemSetup.SpaceCenterFacility fac, string fallback)
        {
            try
            {
                string n = fac.facilityDisplayName;
                if (!string.IsNullOrEmpty(n)) n = KSP.Localization.Localizer.Format(n);
                if (!string.IsNullOrEmpty(n) && !n.StartsWith("#")) return n;
            }
            catch { }
            return fallback;
        }

        /// <summary>A building's box in its own frame (renderer bounds), centre in world space.</summary>
        private static bool BuildingBox(Transform bt, out Vector3d centre, out Vector3 extents, out Quaternion rot)
        {
            return BuildingBox(bt, out centre, out extents, out rot, out _);
        }

        /// <summary>
        /// The building's box in its own frame, from its solid colliders (the physical building),
        /// else from plain mesh renderers. Each collider counts with its own oriented box (box
        /// collider size, mesh collider mesh bounds), not its world-axis bounds: KSP's world axes
        /// are tilted at the Space Center, and the world box of a wide flat slab is tall - the SPH
        /// measured 259 m high and its ground was read far out on the coast. The ground slabs and
        /// aprons around a building (flat boxes, under 8 m high) are left out when it has others.
        /// </summary>
        private static bool BuildingBox(Transform bt, out Vector3d centre, out Vector3 extents, out Quaternion rot, out string how)
        {
            centre = Vector3d.zero; extents = Vector3.zero; rot = bt.rotation; how = "none";
            CelestialBody home = FlightGlobals.GetHomeBody();
            Vector3 upW = home != null ? ((Vector3)((Vector3d)bt.position - home.position)).normalized : bt.up;
            var boxes = new System.Collections.Generic.List<OwnBox>();
            var slabs = new System.Collections.Generic.List<OwnBox>();
            foreach (Collider c in bt.GetComponentsInChildren<Collider>(false))
            {
                if (c == null || !c.enabled || c.isTrigger) continue;
                OwnBox b = ColliderBox(c);
                if (b.Height(upW) < 8f) slabs.Add(b);
                else boxes.Add(b);
            }
            string src = "colliders";
            if (boxes.Count == 0 && slabs.Count > 0) { boxes = slabs; src = "flat colliders"; }
            else if (slabs.Count > 0) src = "colliders, " + slabs.Count + " flat ones left out";
            if (boxes.Count == 0)
            {
                src = "meshes";
                foreach (Renderer r in bt.GetComponentsInChildren<Renderer>(false))
                {
                    if (!(r is MeshRenderer) || !r.enabled) continue;
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) boxes.Add(new OwnBox(r.name, r.transform, mf.sharedMesh.bounds));
                    else boxes.Add(new OwnBox(r.name, null, r.bounds));
                }
            }
            Quaternion inv = Quaternion.Inverse(rot);
            bool any = false;
            Bounds local = new Bounds();
            string top = "";
            float topY = float.NegativeInfinity;
            Vector3 upL = inv * upW;
            foreach (OwnBox ob in boxes)
            {
                for (int i = 0; i < 8; i++)
                {
                    Vector3 l = inv * (ob.Corner(i) - bt.position);
                    if (!any) { local = new Bounds(l, Vector3.zero); any = true; }
                    else local.Encapsulate(l);
                    float hy = Vector3.Dot(l, upL);
                    if (hy > topY) { topY = hy; top = ob.Name; }
                }
            }
            if (!any || local.extents.magnitude > 2000f || local.extents.magnitude < 1f) return false;
            centre = (Vector3d)(bt.position + rot * local.center);
            extents = local.extents;
            how = src + " (" + boxes.Count + ", top: " + top + ")";
            return true;
        }

        private struct OwnBox
        {
            public string Name; public Transform T; public Bounds B;   // B in T's frame (world when T is null)
            public OwnBox(string n, Transform t, Bounds b) { Name = n; T = t; B = b; }

            public Vector3 Corner(int i)
            {
                Vector3 lc = B.center + Vector3.Scale(B.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                return T != null ? T.TransformPoint(lc) : lc;
            }

            /// <summary>The box's height along a world direction.</summary>
            public float Height(Vector3 up)
            {
                float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                for (int i = 0; i < 8; i++) { float h = Vector3.Dot(Corner(i), up); lo = Mathf.Min(lo, h); hi = Mathf.Max(hi, h); }
                return hi - lo;
            }
        }

        private static OwnBox ColliderBox(Collider c)
        {
            try
            {
                if (c is BoxCollider bx) return new OwnBox(c.name, c.transform, new Bounds(bx.center, bx.size));
                if (c is MeshCollider mc && mc.sharedMesh != null) return new OwnBox(c.name, c.transform, mc.sharedMesh.bounds);
                if (c is SphereCollider sp) return new OwnBox(c.name, c.transform, new Bounds(sp.center, Vector3.one * (2f * sp.radius)));
            }
            catch { }
            return new OwnBox(c.name, null, c.bounds);
        }

        /// <summary>Half-width of a box (own frame) along a world direction.</summary>
        private static double Support(Vector3 ext, Quaternion rot, Vector3d dir)
        {
            Vector3 d = Quaternion.Inverse(rot) * (Vector3)dir.normalized;
            return Math.Abs(ext.x * d.x) + Math.Abs(ext.y * d.y) + Math.Abs(ext.z * d.z);
        }

        /// <summary>The launch's opening shot: just outside the VAB / SPH entrance, facing the craft.</summary>
        private bool DoorPose(Vessel v, Vector3d anchor, out Vector3d off, out Quaternion rot, out Vector3 ray, out double tanV)
        {
            off = Vector3d.zero; rot = Quaternion.identity; ray = Vector3.forward;
            tanV = Math.Tan(S.cameraFov * 0.5 * Math.PI / 180.0);
            var fac = Facility(launchFacility == EditorFacility.SPH ? "SpaceplaneHangar" : "VehicleAssemblyBuilding");
            if (fac == null || fac.facilityTransform == null) return false;
            if (!BuildingBox(fac.facilityTransform, out Vector3d bc, out Vector3 ext, out Quaternion brot)) return false;
            Vector3d up = (bc - v.mainBody.position).normalized;
            Vector3d toV = anchor - bc;
            Vector3d h = toV - up * Vector3d.Dot(toV, up);
            if (h.sqrMagnitude < 1.0) return false;
            h = h.normalized;
            double support = Support(ext, brot, h), halfH = Support(ext, brot, up);
            // the height from the real ground outside the entrance (the mesh box reaches below it)
            Vector3d d0 = bc + h * (support + 12.0);
            CelestialBody b = v.mainBody;
            double gD = BaseGround(b, d0, bc);
            double vis = Math.Max(10.0, Math.Min((bc - b.position).magnitude + halfH - gD, 2.0 * halfH));
            double lift = Clamp(0.15 * vis, 5.0, 15.0);
            // the SPH's craft is far down the runway, past the buildings and the ground between:
            // its shot starts well up, looking down the runway
            if (launchFacility == EditorFacility.SPH)
            {
                Vector3d flat = anchor - d0;
                flat = flat - up * Vector3d.Dot(flat, up);
                lift = Math.Max(lift, Clamp(0.2 * flat.magnitude, 60.0, 200.0));
            }
            Vector3d door = b.position + (d0 - b.position).normalized * (gD + lift);
            Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "launch shot: entrance ground {0:F0} m, camera {1:F0} m above it, {2:F0} m from the craft ({3})",
                gD - b.Radius, (door - b.position).magnitude - gD, (door - anchor).magnitude, launchFacility));
            if ((door - anchor).magnitude < 50.0) return false;
            if ((door - anchor).magnitude > 5000.0) return false;   // another launch site: the aerial shot instead
            off = door - anchor;
            ray = (Vector3)ShotSolver.Ray(0.0, -0.05, tanV, CameraRig.Aspect);
            rot = ShotSolver.AimAnyRay(anchor - door, (Vector3d)ray, up);
            return true;
        }

        private static CelestialBody NearestBody(Vector3d p, CelestialBody fallback)
        {
            CelestialBody best = fallback;
            double bestD = double.MaxValue;
            foreach (CelestialBody b in FlightGlobals.Bodies)
            {
                if (b == null) continue;
                double d = (b.position - p).magnitude - b.Radius;
                if (d < bestD) { bestD = d; best = b; }
            }
            return best;
        }

        // ------------------------------------------------------------------ starting

        /// <summary>Flight scene departure: the camera rig around the departing craft.</summary>
        private void BeginFromFlight(Vessel v, FlightCamera fc, GameScenes scene, Kind k)
        {
            var r = new CameraRig(S, fc);
            departVessel = v;
            BeginCommon(r, () => r.Anchor(v), () => departVessel != null && FlightCamera.fetch == fc,
                        v.mainBody, Shots.VesselSize(v), true, scene, k);
        }

        private void BeginCommon(ICamDriver driver, Func<Vector3d> anchorFn, Func<bool> alive, CelestialBody fromBody,
                                 double size, bool fromFlight, GameScenes scene, Kind k)
        {
            kind = k;
            targetScene = scene;
            departBody = fromBody;
            departAnchor = anchorFn;
            departAlive = alive;
            departFromFlight = fromFlight;
            t = 0;
            loadIssued = sceneArrived = false;
            settle = 0;
            clickAt = Time.realtimeSinceStartup;
            measuring = false;
            usableFrames = 0;
            stallFrames = 0;
            maxStall = 0f;
            liveAt = -1;
            waitUnpack = unpackLogged = false;
            retStart = -1;
            riseRequired = false;
            Director.AbortActive("scene transition");

            drv = driver;
            drv.TakenAway = () => { if (phase == Phase.Depart) t = 1e9; };   // jump straight to the capture
            Vector3d anchor0 = anchorFn();
            departStart = drv.Current(anchor0);
            flightLam = null;
            if (staticDepart) { turnSeconds = 0.0; travelSeconds = S.launchHoldSeconds; }
            else PlanFlight(size, anchor0);

            InputLockManager.SetControlLock(ControlTypes.All, LockId);
            HideHud();
            if (fromFlight)
            {
                if (MapView.MapIsEnabled) MapView.ExitMapView();
                try
                {
                    if (CameraManager.Instance != null && CameraManager.Instance.currentCameraMode != CameraManager.CameraMode.Flight)
                        CameraManager.Instance.SetCameraFlight();
                }
                catch { }
            }
            HoldVessels();
            drv.Take();
            // the main menu has no game time (and keeps its animation); launches have nothing in
            // motion to hold (the editor has no physics) - and the music plays on until the switch
            if (k != Kind.SaveLoad && k != Kind.Launch && k != Kind.EditorExit) FreezeTime();
            musicWasPlaying = MusicPlaying();
            musicLogged = false;
            overlay = new Overlay(S, titleText, detailText, kind == Kind.VesselSwitch ? titleText : "", true);
            frozenZoom = 1f;
            frozenPlanetOnScreen = false;
            phase = Phase.Depart;
            if (S.preloadScene) ScenePreload.Start(scene);
        }

        /// <summary>A scene load that skips the transition hook.</summary>
        internal static void LoadDirect(GameScenes scene)
        {
            bypass = true;
            HighLogic.LoadScene(scene);
        }

        /// <summary>Flight -> Space Center / Tracking Station: fly to the home planet's shot.</summary>
        private void BeginBase(Vessel v, FlightCamera fc, GameScenes scene, string place)
        {
            CelestialBody body = FlightGlobals.GetHomeBody();
            titleText = ArrivalText.BodyName(body);
            detailText = place + "\n" + SafeDate();
            framedBody = body;
            lowerThird = false;

            double sx = 2.0 * S.planetScreenX - 1.0;
            if (scene == GameScenes.SPACECENTER)
            {
                // the shot the Space Center scene opens on (carried across the load), with the
                // Space Center on the visible side, so the arrival can zoom down into it
                Vector3d site = KscSite(body);
                Shot ks = ShotSolver.SolveFar(S, site, body, Shots.SunDir(body), CameraRig.Aspect);
                Carry(body, false, ks.Offset, ks.Rot, Vector3.forward, ks.TanV);
                departTarget = anchor =>
                {
                    Vector3d st = KscSite(body);   // live: the frame and the planet move during the flight
                    CamPose p = Shots.Reaim(ks, st, body, sx, ks.TanV);
                    return new CamPose(st + p.Off - anchor, p.Rot, p.TanV);
                };
            }
            else
            {
                carryValid = false;
                Vector3d a0 = (Vector3d)fc.GetPivot().position;
                Shot shot = ShotSolver.SolveFar(S, a0, body, Shots.SunDir(body), CameraRig.Aspect);
                departTarget = anchor => Shots.Reaim(shot, anchor, body, sx, shot.TanV);
                // the Tracking Station then opens on the side of the planet the frozen shot shows
                if (scene == GameScenes.TRACKSTATION && PlanetariumCamera.fetch != null)
                {
                    CamPose e0 = departTarget(a0);
                    AimTrackingOpening(PlanetariumCamera.fetch, a0 + e0.Off - body.position, "the side of the planet shot");
                }
            }
            travelBody = body;
            travelSubject = () => body.position;
            Log.Info("to the " + place + ": flying to " + body.bodyName);
            BeginFromFlight(v, fc, scene, Kind.SpaceCenter);
        }

        private void BeginSwitch(Vessel v, FlightCamera fc, Vessel target)
        {
            PrepareTarget(target);
            Log.Info("switching to " + target.vesselName + " (" + target.mainBody.bodyName + ", " + target.situation + ")");
            BeginFromFlight(v, fc, GameScenes.FLIGHT, Kind.VesselSwitch);
        }

        /// <summary>Texts, the flight's destination and the carried opening shot for a target craft.</summary>
        private void PrepareTarget(Vessel target)
        {
            targetId = target.id;
            switchVessel = target;
            switchVesselSize = Shots.VesselSize(target);
            titleText = target.vesselName;
            detailText = SwitchDetails(target);
            framedBody = null;
            lowerThird = true;
            double sx = 2.0 * S.planetScreenX - 1.0;
            CelestialBody tb = target.mainBody;

            {
                // Fly to the very shot the new scene will start on, wherever the target is.
                Vector3d tp = target.GetWorldPos3D();
                travelBody = tb;
                if (Shots.SurfaceLike(target))
                {
                    travelSubject = () => target.GetWorldPos3D();
                    Shots.Aerial(S, tp, tb, target.altitude, out Vector3d off, out Quaternion rot, out Vector3 ray, out double tan);
                    Carry(tb, true, off, rot, ray, tan);
                    departTarget = anchor =>
                    {
                        CamPose p = Shots.ReaimOnVessel(off, rot, ray, tan);
                        return new CamPose(target.GetWorldPos3D() + p.Off - anchor, p.Rot, p.TanV);
                    };
                }
                else
                {
                    travelSubject = () => tb.position;
                    Shot shot = ShotSolver.Solve(S, tp, tb, Shots.SunDir(tb), Shots.VesselSize(target), CameraRig.Aspect);
                    Carry(tb, false, shot.Offset, shot.Rot, Vector3.forward, shot.TanV);
                    departTarget = anchor =>
                    {
                        Vector3d pos = target.GetWorldPos3D();
                        CamPose p = Shots.Reaim(shot, pos, tb, sx, shot.TanV);
                        return new CamPose(pos + p.Off - anchor, p.Rot, p.TanV);
                    };
                }
            }
        }

        private static Quaternion BodyRotation(CelestialBody b)
        {
            Transform tr = b.bodyTransform != null ? b.bodyTransform : b.transform;
            return tr.rotation;
        }

        /// <summary>Stores a target-relative shot in the planet's own frame (world-frame independent:
        /// the flight scene's world axes differ between scenes, the planet's do not).</summary>
        private void Carry(CelestialBody b, bool aerial, Vector3d off, Quaternion rot, Vector3 ray, double tanV)
        {
            Quaternion inv = Quaternion.Inverse(BodyRotation(b));
            carryBody = b;
            carryAerial = aerial;
            carryOff = (Vector3d)(inv * (Vector3)off);
            carryRot = inv * rot;
            carryRay = ray;
            carryTanV = tanV;
            carryValid = true;
        }

        /// <summary>
        /// The departure: a turn to behind the craft facing the destination (craft in the
        /// foreground), then a flight that closes in on the destination planet's centre at a
        /// constant zoom rate (log distance) with the destination locked on screen, easing into the
        /// final shot. Longer for longer trips.
        /// </summary>
        private void PlanFlight(double size, Vector3d anchor)
        {
            double tanV = Math.Tan(S.cameraFov * 0.5 * Math.PI / 180.0);
            Vector3d subject = travelSubject();
            Vector3d Pd = departBody.position;
            safeRadius = departBody.Radius + Math.Max(0.05 * departBody.Radius, 10000.0);   // above any terrain
            double rv = Math.Max(1.0, (anchor - Pd).magnitude);
            Vector3d uV = (anchor - Pd) / rv;
            bool sameBody = travelBody == departBody;

            // Default turn: just behind the craft, facing the destination, craft in the foreground.
            Vector3d toward = (subject - anchor).normalized;
            Vector3d up = (Vector3d)(departStart.Rot * Vector3.up);
            up = up - toward * Vector3d.Dot(up, toward);
            if (up.sqrMagnitude < 1e-6) up = ShotSolver.Perp(toward);
            up = up.normalized;
            double back = Math.Max(30.0, 7.0 * size);
            Vector3d off = -toward * back + up * (0.22 * back);

            // Low down (landed, flying, very low orbit), or the destination below the horizon:
            // first rise to a clear point well above the terrain, swung around the planet just far
            // enough that the way to the destination clears it; only then the zoom.
            bool low = rv < safeRadius;
            bool blocked = !sameBody && !SegmentClears(anchor + off, subject, Pd, safeRadius);
            bool clear = !noRise && (low || blocked);
            riseRequired = clear;
            if (clear)
            {
                double rm = Math.Max(rv, 1.3 * safeRadius);
                Vector3d dirM = uV;
                if (!sameBody)
                {
                    Vector3d tdir = (subject - Pd).normalized;
                    double theta = Math.Acos(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(uV, tdir))));
                    double thetaMax = Math.PI - Math.Asin(Math.Min(1.0, safeRadius / rm)) - 5.0 * Math.PI / 180.0;
                    if (theta > thetaMax)
                    {
                        Vector3d axis = Vector3d.Cross(uV, tdir);
                        if (axis.sqrMagnitude < 1e-10) axis = Vector3d.Cross(uV, up);
                        if (axis.sqrMagnitude < 1e-10) axis = ShotSolver.Perp(uV);
                        dirM = ShotSolver.Rotate(uV, axis.normalized, theta - thetaMax).normalized;
                    }
                }
                off = Pd + dirM * rm - anchor;
                up = dirM;
            }
            Quaternion rot = ShotSolver.AimAnyRay(subject - (anchor + off), new Vector3d(0, 0, 1), up);
            departMid = new CamPose(off, rot, tanV);

            CamPose end = departTarget(anchor);
            Vector3d P = TravelCenter();
            double dm = Math.Max(1.0, (anchor + off - P).magnitude);
            double de = Math.Max(1.0, (anchor + end.Off - P).magnitude);
            double decades = Math.Abs(Math.Log(dm / de));
            turnSeconds = S.departTurnSeconds;
            if (noRise) turnSeconds = Math.Min(turnSeconds, 1.0);   // a short hop: a quick turn
            if (clear)   // a longer rise gets a little more time
                turnSeconds += Math.Min(2.0, 0.2 * Math.Log(Math.Max(1.0, off.magnitude / Math.Max(1.0, departStart.Off.magnitude))));
            travelSeconds = Math.Min(S.departMaxSeconds, S.departSeconds + 0.35 * decades);
            RetimeFlight(anchor);
            Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "flight: {0}{1:F0} km to {2:F0} km from {3}'s centre, {4:F1} s",
                clear ? (low ? "rise first (low), " : "rise first (destination behind the planet), ") : "",
                dm / 1000.0, de / 1000.0, travelBody.bodyName, turnSeconds + travelSeconds));
        }

        private Vector3d TravelCenter() { return travelCenter != null ? travelCenter() : travelBody.position; }

        /// <summary>Does the straight line from a to b stay outside the sphere?</summary>
        private static bool SegmentClears(Vector3d a, Vector3d b, Vector3d centre, double radius)
        {
            Vector3d d = b - a;
            double l2 = d.sqrMagnitude;
            double t = l2 > 0 ? Math.Max(0.0, Math.Min(1.0, Vector3d.Dot(centre - a, d) / l2)) : 0.0;
            return (a + d * t - centre).magnitude >= radius;
        }

        /// <summary>Pushes a point radially out of a sphere.</summary>
        private static Vector3d OutsideSphere(Vector3d p, Vector3d centre, double radius)
        {
            Vector3d rel = p - centre;
            double r = rel.magnitude;
            return r < radius && r > 1.0 ? centre + rel * (radius / r) : p;
        }

        /// <summary>Safety net above the actual terrain (not the sphere), soft so it never shows as a
        /// glide along a fixed height.</summary>
        private Vector3d TerrainClamp(Vector3d cam)
        {
            try { return SoftGround(departBody, cam, 4.0, 8.0); }
            catch { return cam; }
        }

        /// <summary>Vessel card: type and situation, Ap/Pe when in space, crew, mission time.</summary>
        private static string SwitchDetails(Vessel v)
        {
            var lines = new System.Collections.Generic.List<string>();
            string sit;
            try { sit = Vessel.GetSituationString(v); } catch { sit = v.situation.ToString(); }
            lines.Add(sit);
            try
            {
                if (v.orbit != null && (v.situation == Vessel.Situations.ORBITING || v.situation == Vessel.Situations.SUB_ORBITAL
                                        || v.situation == Vessel.Situations.ESCAPING))
                {
                    string pe = "Pe " + ArrivalText.Dist(v.orbit.PeA);
                    lines.Add(v.orbit.eccentricity < 1.0 ? "Ap " + ArrivalText.Dist(v.orbit.ApA) + "   \u00b7   " + pe : pe);
                }
            }
            catch { }
            int crew = 0;
            try { crew = v.loaded ? v.GetCrewCount() : (v.protoVessel != null ? v.protoVessel.GetVesselCrew().Count : 0); } catch { }
            if (crew > 0) lines.Add(crew + (crew == 1 ? " Kerbal aboard" : " Kerbals aboard"));
            string mt = ArrivalText.MissionTime(v.missionTime);
            if (mt != null) lines.Add(mt);
            return string.Join("\n", lines.ToArray());
        }

        private static string SafeDate()
        {
            try { return KSPUtil.PrintDate(Planetarium.GetUniversalTime(), false); } catch { return ""; }
        }

        /// <summary>Hide orbit lines, icons and nodes in the CURRENT scene; the snapshot is restored on cleanup.</summary>
        private void HideOrbits()
        {
            if (sceneOrbits != null) return;
            sceneOrbits = OrbitLines.Hide();
        }

        private void RestoreOrbits()
        {
            if (sceneOrbits == null) return;
            sceneOrbits.Restore();
            sceneOrbits = null;
        }

        // ------------------------------------------------------------------ frame loop

        private void Update()
        {
            WatchStockMask();
            ScenePreload.Poll(phase == Phase.Idle);
            HideModHuds();
            if (phase == Phase.Idle)
            {
                if (double.IsNaN(kscLat) && HighLogic.LoadedScene == GameScenes.SPACECENTER) RememberKsc();
                return;
            }
            if (measuring)
            {
                float dt = Time.unscaledDeltaTime;
                if (dt > 0.1f) { stallFrames++; if (dt > maxStall) maxStall = dt; smooth = 0; }
                else smooth++;
            }
            try { Step(); }
            catch (Exception e)
            {
                Log.Warn("transition failed, loading normally: " + e);
                Fail();
            }
        }

        private void LateUpdate()
        {
            if (phase == Phase.ArriveKsc) { try { KscApply(false); } catch { } return; }
            if (phase != Phase.Depart && phase != Phase.Capture && phase != Phase.Arrive) return;
            try { ApplyCurrent(false); } catch { }
        }

        private void Step()
        {
            switch (phase)
            {
                case Phase.Depart:
                    t += Math.Min(Time.unscaledDeltaTime, 1.0 / 30.0);
                    WatchMusic();
                    HoldVessels();
                    if (!departAlive()) { Freeze(false); break; }
                    ApplyCurrent(true);
                    if (t >= turnSeconds + travelSeconds)
                    {
                        phase = Phase.Capture;
                        overlay.BeginSceneCapture();
                        StartCoroutine(CaptureAtEndOfFrame());
                    }
                    break;

                case Phase.Capture:
                    HoldVessels();
                    ApplyCurrent(true);
                    break;

                case Phase.PendingLoad:
                    KscTerrainOn();
                    Unpause();
                    phase = Phase.Loading;
                    t = 0;
                    loadIssued = true;
                    loadAt = Time.realtimeSinceStartup;
                    measuring = true;
                    stockEndPending = false;
                    bypass = true;
                    HighLogic.LoadScene(targetScene);
                    break;

                case Phase.Loading:
                    t += Time.unscaledDeltaTime;
                    LoadingFrame();
                    break;

                case Phase.Arrive:
                    ArriveStep();
                    break;

                case Phase.ArriveKsc:
                    KscStep();
                    break;

                case Phase.Reveal:
                    t += Math.Min(Time.unscaledDeltaTime, 1.0 / 30.0);
                    RevealFrame();
                    break;
            }
        }

        /// <summary>Depart / Arrive camera pose for the current time.</summary>
        private void ApplyCurrent(bool withOverlay)
        {
            if (phase == Phase.Arrive) { ArriveApply(withOverlay); return; }
            if (staticDepart)
            {
                if (withOverlay) overlay.Apply(DepartCard(Vector3d.zero, Quaternion.identity, 1.0));
                return;
            }
            Vector3d anchor = departAnchor();
            DepartPoseAt(FlightU(t), anchor, out Vector3d cam, out Quaternion rot, out double tanV, out double weight);
            cam = TerrainClamp(cam);
            drv.Apply(cam, rot, tanV, weight);
            if (HighLogic.LoadedScene == GameScenes.SPACECENTER && drv is CameraRig)
                KscTerrainSwitch(FlightGlobals.GetHomeBody(), cam);
            lastCam = cam; lastRot = rot; lastTanV = tanV;
            if (withOverlay)
                overlay.Apply(DepartCard(cam, rot, tanV));
        }

        /// <summary>The departure's camera pose at path position u (0..1): the turn (to departMid)
        /// then the zoom, or one continuous pan when no rise is needed.</summary>
        private void DepartPoseAt(double u, Vector3d anchor, out Vector3d cam, out Quaternion rot, out double tanV, out double weight)
        {
            Vector3d Pd = departBody.position;
            if (!riseRequired)
            {
                // A single continuous pan: straight from the current pose to the final shot.
                CamPose end = departTarget(anchor);
                Vector3d endCam = anchor + end.Off;
                Vector3d P = TravelCenter();
                Vector3d um = anchor + departStart.Off - P, ue = endCam - P;
                double dm = Math.Max(1.0, um.magnitude), de = Math.Max(1.0, ue.magnitude);
                double dist = Math.Exp(Math.Log(dm) + (Math.Log(de) - Math.Log(dm)) * u);
                Vector3 dir = Vector3.Slerp((Vector3)(um / dm), (Vector3)(ue / de), (float)u).normalized;
                cam = P + (Vector3d)dir * dist;

                // destination locked on screen: from its spot in the current view to its final spot
                Vector3d subject = travelSubject();
                Vector3 endRay = (Quaternion.Inverse(end.Rot) * (Vector3)(subject - endCam).normalized).normalized;
                Vector3d startCam = anchor + departStart.Off;
                Vector3 startRay = (Quaternion.Inverse(departStart.Rot) * (Vector3)(subject - startCam).normalized).normalized;
                if (startRay.z < 0.2f) startRay = Vector3.forward;   // out of view: turned to below
                Vector3 ray = Vector3.Lerp(startRay, endRay, (float)u).normalized;
                Vector3 up = Vector3.Slerp(departStart.Rot * Vector3.up, end.Rot * Vector3.up, (float)u);
                rot = ShotSolver.AimAnyRay(subject - cam, (Vector3d)ray, (Vector3d)up);
                // the very first frame is the current view: what aiming leaves over (a roll, or the
                // whole turn when the destination was out of view) eases out over the first half
                Quaternion rot0 = ShotSolver.AimAnyRay(subject - startCam, (Vector3d)startRay, (Vector3d)(departStart.Rot * Vector3.up));
                Quaternion fix = Quaternion.Inverse(rot0) * departStart.Rot;
                rot = rot * Quaternion.Slerp(fix, Quaternion.identity, Overlay.Ease((float)Math.Min(1.0, u / 0.5)));
                if (u > 0.85) rot = Quaternion.Slerp(rot, end.Rot, Overlay.Ease((float)((u - 0.85) / 0.15)));
                tanV = Math.Exp(Math.Log(departStart.TanV) + (Math.Log(end.TanV) - Math.Log(departStart.TanV)) * u);
                weight = 0.3 + 0.7 * u;

                double floor = Math.Min(safeRadius, (anchor + departStart.Off - Pd).magnitude);
                if (departBody == travelBody) floor = Math.Min(floor, (endCam - Pd).magnitude);
                // planet-scale flights only: a hop to a building is kept up by the soft terrain floor
                if (!noRise) cam = OutsideSphere(cam, Pd, floor);
            }
            else if (u < flightUMid)
            {
                double uMid = flightUMid;
                double s = uMid > 0.0 ? Math.Min(1.0, u / uMid) : 1.0;
                CamPose p = CamPose.Blend(departStart, departMid, s);
                cam = anchor + p.Off; rot = p.Rot; tanV = p.TanV; weight = 0.3 * s;
                // never lower than where the camera started (or the clear height, if it started above it)
                double floor = Math.Min(safeRadius, Math.Min((anchor + departStart.Off - Pd).magnitude, (anchor + departMid.Off - Pd).magnitude));
                cam = OutsideSphere(cam, Pd, floor);
            }
            else
            {
                double uMid = flightUMid;
                double s = Math.Min(1.0, Math.Max(0.0, (u - uMid) / Math.Max(0.0001, 1.0 - uMid)));
                CamPose end = departTarget(anchor);
                Vector3d endCam = anchor + end.Off;
                Vector3d P = TravelCenter();
                Vector3d um = anchor + departMid.Off - P, ue = endCam - P;
                double dm = Math.Max(1.0, um.magnitude), de = Math.Max(1.0, ue.magnitude);
                double dist = Math.Exp(Math.Log(dm) + (Math.Log(de) - Math.Log(dm)) * s);
                Vector3 dir = Vector3.Slerp((Vector3)(um / dm), (Vector3)(ue / de), (float)s).normalized;
                cam = P + (Vector3d)dir * dist;

                // destination locked on screen: from the centre of the view to its final spot
                Vector3d subject = travelSubject();
                Vector3 endRay = (Quaternion.Inverse(end.Rot) * (Vector3)(subject - endCam).normalized).normalized;
                Vector3 ray = Vector3.Lerp(Vector3.forward, endRay, (float)s).normalized;
                Vector3 up = Vector3.Slerp(departMid.Rot * Vector3.up, end.Rot * Vector3.up, (float)s);
                rot = ShotSolver.AimAnyRay(subject - cam, (Vector3d)ray, (Vector3d)up);
                if (s > 0.85) rot = Quaternion.Slerp(rot, end.Rot, Overlay.Ease((float)((s - 0.85) / 0.15)));
                tanV = Math.Exp(Math.Log(departMid.TanV) + (Math.Log(end.TanV) - Math.Log(departMid.TanV)) * s);
                weight = 0.3 + 0.7 * s;

                // safety net: never below the clear height of the planet being left (or, when it is
                // also the destination, never below the lower of the two ends of the flight)
                double floor = Math.Min(safeRadius, (anchor + departMid.Off - Pd).magnitude);
                if (departBody == travelBody) floor = Math.Min(floor, (endCam - Pd).magnitude);
                cam = OutsideSphere(cam, Pd, floor);
            }
        }

        // ------------------------------------------------------------------ flight timing

        // The path is walked by "perceived distance": the log of the distance to the nearer of the
        // two planets plus the turning of the view. Driving the old S-curve straight in path position
        // left a far planet at a thousandfold distance change in the first moments (the path is a log
        // zoom on the destination): a jerk through several planets. Now speed and acceleration along
        // the perceived distance are capped (flightMaxRate, flightMaxAccel): long trips start and end
        // gently and take a few seconds more.
        private double[] flightLam;            // perceived distance at u = i / (n - 1)
        private double flightUMid;             // path position where the turn ends (rise flights)
        private double flightRamp, flightPeak, flightLen, flightProfileT, flightT;

        private double FlightU(double time)
        {
            if (flightLam == null || flightLen <= 1e-9)
                return Overlay.Ease((float)Math.Min(1.0, time / Math.Max(0.0001, turnSeconds + travelSeconds)));
            double tau = Math.Min(flightProfileT, Math.Max(0.0, time * flightProfileT / Math.Max(1e-6, flightT)));
            double lam = ProfileAt(tau);
            // invert the table
            int n = flightLam.Length;
            if (lam <= 0.0) return 0.0;
            if (lam >= flightLen) return 1.0;
            int lo = 0, hi = n - 1;
            while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (flightLam[mid] < lam) lo = mid; else hi = mid; }
            double a = flightLam[lo], b = flightLam[hi];
            double f = b > a ? (lam - a) / (b - a) : 0.0;
            return (lo + f) / (n - 1);
        }

        /// <summary>Distance along the profile: a cosine ramp up to the peak rate (acceleration at
        /// most flightMaxAccel), a cruise, the same ramp down.</summary>
        private double ProfileAt(double tau)
        {
            double Ta = flightRamp, Vp = flightPeak, T = flightProfileT;
            if (tau <= 0.0) return 0.0;
            if (tau >= T) return flightLen;
            if (tau < Ta) return Vp * (0.5 * tau - Ta / (2.0 * Math.PI) * Math.Sin(Math.PI * tau / Ta));
            double rampLen = 0.5 * Vp * Ta;
            if (tau <= T - Ta) return rampLen + Vp * (tau - Ta);
            double r = T - tau;
            return flightLen - Vp * (0.5 * r - Ta / (2.0 * Math.PI) * Math.Sin(Math.PI * r / Ta));
        }

        /// <summary>Measures the planned path and sets its timing (turnSeconds + travelSeconds).</summary>
        private void RetimeFlight(Vector3d anchor)
        {
            flightLam = null;
            double total0 = Math.Max(0.0001, turnSeconds + travelSeconds);
            flightUMid = Overlay.Ease((float)Math.Min(1.0, turnSeconds / total0));
            try
            {
                const int n = 301;
                var lam = new double[n];
                Vector3d Po = departBody.position;
                Vector3d Pt = TravelCenter();
                bool one = travelBody == departBody;
                Vector3d prevCam = Vector3d.zero; Vector3 prevFwd = Vector3.forward; double prevTan = 1.0, prevNear = 1.0;
                for (int i = 0; i < n; i++)
                {
                    double u = (double)i / (n - 1);
                    DepartPoseAt(u, anchor, out Vector3d cam, out Quaternion rot, out double tanV, out _);
                    double near = (cam - Pt).magnitude;
                    if (!one) near = Math.Min(near, (cam - Po).magnitude);
                    near = Math.Max(1.0, near);
                    Vector3 fwd = rot * Vector3.forward;
                    if (i == 0) lam[i] = 0.0;
                    else
                    {
                        double step = Math.Abs(Math.Log(near / prevNear))
                                      + Vector3.Angle(prevFwd, fwd) * Math.PI / 180.0
                                      + Math.Abs(Math.Log(tanV / prevTan));
                        lam[i] = lam[i - 1] + step;
                    }
                    prevCam = cam; prevFwd = fwd; prevTan = tanV; prevNear = near;
                }
                double L = lam[n - 1];
                double V = Math.Max(0.1, S.flightMaxRate), A = Math.Max(0.05, S.flightMaxAccel);
                double Ta = V * Math.PI / (2.0 * A);
                if (L < V * Ta) { V = Math.Sqrt(2.0 * A * L / Math.PI); Ta = V * Math.PI / (2.0 * A); }
                double Tc = V > 0.0 ? Math.Max(0.0, (L - V * Ta) / V) : 0.0;
                double Tp = 2.0 * Ta + Tc;
                if (L < 1e-6 || Tp < 1e-3) return;
                flightLam = lam;
                flightLen = L; flightPeak = V; flightRamp = Ta; flightProfileT = Tp;
                // never quicker than before, never longer than departMaxSeconds (a capped trip runs
                // the same profile a little faster)
                double T = Math.Min(Math.Max(total0, Tp), Math.Max(total0, S.departMaxSeconds));
                flightT = T;
                travelSeconds = Math.Max(0.1, T - turnSeconds);
                if (turnSeconds > T - 0.1) { turnSeconds = 0.5 * T; travelSeconds = T - turnSeconds; }
                Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "flight timing: {0:F1} e-folds of view change, {1:F1} s (was {2:F1} s; peak {3:F2}/s, ramps {4:F1} s{5})",
                    L, T, total0, V * Tp / T, Ta * T / Tp, Tp > T + 0.05 ? ", sped up to fit departMaxSeconds" : ""));
            }
            catch (Exception e) { flightLam = null; Log.Warn("flight timing: " + e.Message); }
        }

        /// <summary>The title card during the flight: it fades in over the last part of the zoom, so
        /// the destination is named as it arrives (the load itself is often much shorter).</summary>
        private OverlayFrame DepartCard(Vector3d cam, Quaternion rot, double tanV)
        {
            double sT = t <= turnSeconds ? 0.0 : Math.Min(1.0, (t - turnSeconds) / travelSeconds);
            var f = new OverlayFrame
            {
                Bars = (float)(t / 1.2),
                LowerThird = lowerThird,
                TitleAlpha = Mathf.Clamp01((float)((sT - 0.55) / 0.2)),
                DetailAlpha = Mathf.Clamp01((float)((sT - 0.65) / 0.2)),
                Line = Mathf.Clamp01((float)((sT - 0.5) / 0.3)),
            };
            f.MarkerAlpha = f.TitleAlpha;
            if (framedBody != null)
                f.PlanetOnScreen = CameraRig.ProjectSphere(cam, rot, tanV, framedBody.position, framedBody.Radius, out f.Planet, out f.PlanetRadius);
            if (switchVessel != null && S.showVesselMarker)
                f.MarkerOnScreen = CameraRig.ProjectMarker(cam, rot, tanV, switchVessel.GetWorldPos3D(), switchVesselSize,
                                                           switchVessel.mainBody, S.barFraction, out f.Marker);
            return f;
        }

        private IEnumerator CaptureAtEndOfFrame()
        {
            yield return new WaitForEndOfFrame();
            Freeze(true);
        }

        /// <summary>Freezes the last frame on the overlay and lets the load go.</summary>
        private void Freeze(bool capture)
        {
            if (phase != Phase.Depart && phase != Phase.Capture) return;
            try
            {
                if (capture)
                {
                    Texture2D tex = overlay.TakeSceneCapture() ?? ScreenCapture.CaptureScreenshotAsTexture();
                    var px = tex.GetPixels32();
                    for (int i = 0; i < px.Length; i++) px[i].a = 255;   // the back buffer's alpha is not reliable
                    tex.SetPixels32(px);
                    tex.Apply(false, true);
                    overlay.SetFrozen(tex);
                }
                if (framedBody != null)
                    frozenPlanetOnScreen = CameraRig.ProjectSphere(lastCam, lastRot, lastTanV, framedBody.position, framedBody.Radius,
                                                                   out frozenPlanet, out frozenPlanetRadius);
                cardAtFreeze = DepartCard(lastCam, lastRot, lastTanV);
                Log.Info("frozen; " + overlay.Describe());
                RecordTrackingPose();
                switchVessel = null;   // destroyed with the old scene
                overlay.Apply(LoadingOverlay(0));
            }
            catch (Exception e) { Log.Warn("frame capture: " + e.Message); }
            try { drv.Leave(); } catch { }
            reenableOnFail = null;   // the scene is about to go
            phase = Phase.PendingLoad;
        }

        // ------------------------------------------------------------------ loading

        private OverlayFrame LoadingOverlay(double tl)
        {
            // No push-in: the frozen frame now contains the card, which must stay aligned with
            // the live one drawn over it.
            float z = 1f;
            frozenZoom = z;
            var f = new OverlayFrame
            {
                Bars = 1f,
                FrozenAlpha = 1f,
                FrozenZoom = z,
                LowerThird = lowerThird || !frozenPlanetOnScreen,
                TitleAlpha = Mathf.Max(cardAtFreeze.TitleAlpha, Mathf.Clamp01((float)((tl - 0.3) / 0.8))),
                DetailAlpha = Mathf.Max(cardAtFreeze.DetailAlpha, Mathf.Clamp01((float)((tl - 0.7) / 0.8))),
                MarkerOnScreen = cardAtFreeze.MarkerOnScreen,
                Marker = cardAtFreeze.Marker,
                MarkerAlpha = Mathf.Max(cardAtFreeze.MarkerAlpha, Mathf.Clamp01((float)((tl - 0.3) / 0.8))),
            };
            f.Line = Mathf.Max(cardAtFreeze.Line, Mathf.Clamp01((float)(tl / 1.0)));
            if (frozenPlanetOnScreen)
            {
                f.FrozenPivot = new Vector2(frozenPlanet.x / Screen.width, frozenPlanet.y / Screen.height);
                f.PlanetOnScreen = true;
                f.Planet = frozenPlanet;
                f.PlanetRadius = frozenPlanetRadius * z;
            }
            else f.FrozenPivot = new Vector2(0.5f, 0.5f);
            return f;
        }

        private void LoadingFrame()
        {
            overlay.Apply(LoadingOverlay(t));
            MatchTrackingPose(false);
            if (sceneArrived && !sceneHudHidden)
            {
                sceneHudHidden = true;
                Milestone("scene loaded");
                SuppressMask();
                HideHud();
                // Space Center / Tracking Station: their camera controllers only update while
                // camera controls are unlocked - keep those free so the camera is at its default
                // when the card dissolves (it snapped there at the end before).
                if (targetScene != GameScenes.FLIGHT)
                {
                    InputLockManager.RemoveControlLock(LockId);
                    InputLockManager.SetControlLock(ControlTypes.All & ~(ControlTypes.CAMERACONTROLS | ControlTypes.CAMERAMODES), LockId);
                }
            }
            // The game's loading mask re-shows the canvases on every scene change and again when
            // the vessel is unpacked; keep the HUD off for the whole loading of a new scene.
            KeepHudOff();
            if (sceneArrived) SuppressMask();
            // Start as soon as the scene can be shown (KSP keeps its own loading screen up for a
            // while longer, finishing the load behind it); with live loading off, as before: once
            // KSP's loading screen is gone and the frames are smooth.
            bool go;
            if (S.liveLoading)
            {
                bool usable = sceneArrived && (Usable() || Ready());
                usableFrames = usable ? usableFrames + 1 : 0;
                go = usableFrames >= 5 && smooth >= 3;
            }
            else
            {
                bool ready = sceneArrived && Ready();
                settle = ready && Time.unscaledDeltaTime < 0.05f ? settle + 1 : 0;
                go = settle >= 20;
            }
            if (t > 120.0) { Log.Warn("scene never looked ready - revealing"); BeginReveal(); return; }
            if (!go || t < S.loadingMinSeconds) return;
            Milestone("scene shown");

            Vessel av = FlightGlobals.ActiveVessel;
            if (kind == Kind.VesselSwitch && av != null && av.id == targetId)
                BeginArrive(av);
            else if (kind == Kind.Launch && av != null && HighLogic.LoadedSceneIsFlight)
                BeginArrive(av);
            else if ((kind == Kind.ToKsc || kind == Kind.SaveLoad || kind == Kind.SpaceCenter || kind == Kind.EditorExit) && HighLogic.LoadedScene == GameScenes.SPACECENTER)
                BeginArriveKsc();
            else
                BeginReveal();
        }

        /// <summary>The new scene can be shown and driven (not yet finished loading).</summary>
        private bool Usable()
        {
            switch (targetScene)
            {
                case GameScenes.FLIGHT:
                    Vessel av = FlightGlobals.ActiveVessel;
                    return HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && av != null && av.loaded
                           && FlightCamera.fetch != null && FlightCamera.fetch.GetPivot() != null;
                case GameScenes.SPACECENTER:
                    {
                        if (HighLogic.LoadedScene != GameScenes.SPACECENTER) return false;
                        FlightCamera fc = FlightCamera.fetch;
                        return fc != null && fc.transform.parent != null && fc.transform.parent.name == "CameraTransform"
                               && UnityEngine.Object.FindObjectOfType<SpaceCenterCamera2>() != null;
                    }
                case GameScenes.TRACKSTATION:
                    return HighLogic.LoadedScene == GameScenes.TRACKSTATION && PlanetariumCamera.fetch != null
                           && PlanetariumCamera.fetch.target != null;
                case GameScenes.EDITOR:
                    return HighLogic.LoadedScene == GameScenes.EDITOR && EditorLogic.fetch != null;
                default:
                    return HighLogic.LoadedScene == targetScene;
            }
        }

        private bool Ready()
        {
            if (!MaskHidden()) return false;
            if (targetScene != GameScenes.FLIGHT) return HighLogic.LoadedScene == targetScene;
            return HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && FlightGlobals.ActiveVessel != null
                   && FlightGlobals.ActiveVessel.loaded && FlightCamera.fetch != null && FlightCamera.fetch.GetPivot() != null;
        }

        private static bool MaskHidden()
        {
            try
            {
                var m = MaskInstanceField != null ? MaskInstanceField.GetValue(null) as LoadingBufferMask : null;
                if (m == null || m.camera == null) return true;
                return !m.camera.enabled;
            }
            catch { return true; }
        }

        // ------------------------------------------------------------------ arrive (vessel switch)

        private void BeginArrive(Vessel v)
        {
            FlightCamera fc = FlightCamera.fetch;
            rig = new CameraRig(S, fc);
            retakes = 0;
            retakeRequested = false;
            // Started while the scene still loads, the game may re-target the camera (focusing the
            // craft): take it back on the next frame, a few times, before giving the shot up.
            rig.TakenAway = () =>
            {
                if (phase == Phase.Arrive && retStart < 0 && retakes < 3) retakeRequested = true;
                else EndArrive("camera retargeted");
            };
            arriveVessel = v;
            arrivePlan = null;
            arriveSize = Shots.VesselSize(v);
            CelestialBody b = v.mainBody;
            Vector3d anchor = rig.Anchor(v);
            double sx = 2.0 * S.planetScreenX - 1.0;

            if (kind == Kind.Launch && DoorPose(v, anchor, out Vector3d dOff, out Quaternion dRot, out Vector3 dRay, out double dTan))
            {
                estPose = a => Shots.ReaimOnVessel(dOff, dRot, dRay, dTan);
            }
            else if (carryValid && carryBody == b)
            {
                Quaternion q = BodyRotation(b);
                Vector3d off = (Vector3d)(q * (Vector3)carryOff);
                Quaternion rot = q * carryRot;
                if (carryAerial)
                {
                    Vector3 ray = carryRay;
                    double tan = carryTanV;
                    estPose = a => Shots.ReaimOnVessel(off, rot, ray, tan);
                }
                else
                {
                    var shot = new Shot { Mode = "carried", Offset = off, Rot = rot, TanV = carryTanV };
                    estPose = a => Shots.Reaim(shot, a, b, sx, shot.TanV);
                }
            }
            else if (Shots.SurfaceLike(v))
            {
                Shots.Aerial(S, anchor, b, v.altitude, out Vector3d off, out Quaternion rot, out Vector3 ray, out double tan);
                estPose = a => Shots.ReaimOnVessel(off, rot, ray, tan);
            }
            else
            {
                Shot shot = ShotSolver.Solve(S, anchor, b, Shots.SunDir(b), arriveSize, CameraRig.Aspect);
                estPose = a => Shots.Reaim(shot, a, b, sx, shot.TanV);
            }
            carryValid = false;
            // Near the ground (launch, landed / flying craft) the terrain is still being built until
            // the craft is unpacked: the card stays up until then. In space it starts at once.
            waitUnpack = S.liveLoading && (kind == Kind.Launch || Shots.SurfaceLike(v)) && v.packed;
            if (waitUnpack && HeldByPhysicsHold(v))
            {
                waitUnpack = false;
                Log.Info("the craft is held by PhysicsHold - not waiting for it to unpack");
            }

            titleText = v.vesselName;
            detailText = SwitchDetails(v);
            overlay.SetTexts(titleText, detailText);
            Log.Info("arriving on " + v.vesselName + "; " + overlay.Describe());

            InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS | ControlTypes.CAMERAMODES | ControlTypes.STAGING | ControlTypes.PAUSE, LockId);
            HideHud();
            rig.Take();
            HideOrbits();
            t = 0;
            retStart = -1;
            liveAt = waitUnpack ? -1 : 0;
            unpackLogged = !v.packed;
            phase = Phase.Arrive;
            ArriveApply(true);
        }

        /// <summary>The PhysicsHold mod is installed and its hold is on for this craft: it stays
        /// packed for as long as the hold is on, so there is no unpacking to wait for.</summary>
        private static bool HeldByPhysicsHold(Vessel v)
        {
            try
            {
                Component hold = v.GetComponent("VesselPhysicsHold");
                if (hold == null) return false;
                FieldInfo held = hold.GetType().GetField("physicsHold");
                return held != null && (bool)held.GetValue(hold);
            }
            catch { return false; }
        }

        private void ArriveStep()
        {
            t += Math.Min(Time.unscaledDeltaTime, 1.0 / 30.0);
            Vessel v = arriveVessel;
            if (v == null || FlightGlobals.ActiveVessel != v || rig.Fc != FlightCamera.fetch) { EndArrive("vessel or camera gone"); return; }
            if (MapView.MapIsEnabled || (CameraManager.Instance != null && CameraManager.Instance.currentCameraMode != CameraManager.CameraMode.Flight))
            { EndArrive("camera mode changed"); return; }

            // the loading mask has already shown its UI once; keep the HUD off for the whole zoom
            KeepHudOff();
            if (retakeRequested)
            {
                retakeRequested = false;
                retakes++;
                rig.Retake();
                Log.Info("the game re-targeted the camera while loading - taken back (" + retakes + ")");
            }

            if (!unpackLogged && !v.packed) { unpackLogged = true; Milestone("craft unpacked"); }
            if (liveAt < 0 && (!v.packed || t > 60.0))
            {
                liveAt = t;
                if (v.packed) Log.Warn("craft still packed after 60 s - showing it anyway");
            }

            // After the opening shot, the zoom down to the craft is the hand-back itself. While the
            // game still stalls (finishing its load) the shot holds; the zoom starts on smooth frames.
            if (liveAt >= 0 && retStart < 0)
            {
                double live = t - liveAt;
                if (live > 0.5 && (Input.GetKeyDown(S.skipKey) || Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(0)))
                    StartReturn(Math.Min(S.zoomInSeconds, 2.0));
                else if (live >= S.establishSeconds && (smooth >= 10 || live > 45.0 || !S.liveLoading))
                {
                    Milestone("zoom in");
                    StartReturn(S.zoomInSeconds);
                }
            }
            if (retStart >= 0 && t >= retStart + retDur)
            {
                rig.HandBack();
                Cleanup();
                return;
            }
            ArriveApply(true);
        }

        private void StartReturn(double duration)
        {
            Vector3d anchor = rig.Anchor(arriveVessel);
            retFrom = ArrivePose(anchor);
            if (kind != Kind.Launch && S.keepShotAngle) AimHomeAlongShot(anchor, retFrom);
            CelestialBody b = arriveVessel.mainBody;
            arrivePlan = Paths.Make(retFrom, rig.Home(), anchor, b.position, b.Radius);
            if (arrivePlan != null)
                Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "the craft is behind {0}: zooming around it (in view at {1:F0} % of the zoom)", b.bodyName, arrivePlan.Sw * 100.0));
            retStart = t;
            retDur = duration;
        }

        /// <summary>
        /// The zoom ends on FlightCamera's own resume pose (heading / pitch around the craft in its
        /// frame of reference), which KSP sets on the scene load with no regard to the opening shot -
        /// a different side of the craft made the zoom swing around it. Here that heading and pitch
        /// are set to look at the craft from where the opening shot looks at it, so the zoom is
        /// (almost) a straight push in. Pitch within FlightCamera's own limits; landed / flying
        /// craft (the aerial shot looks down on them) end at most ~35 degrees from above.
        /// </summary>
        private void AimHomeAlongShot(Vector3d anchor, CamPose shot)
        {
            try
            {
                FlightCamera fc = rig.Fc;
                Vector3d look = -shot.Off;
                if (fc == null || look.sqrMagnitude < 1e-6) return;
                Quaternion frame = FlightGlobals.GetFoR(fc.FoRMode);
                Vector3 local = Quaternion.Inverse(frame) * (Vector3)look.normalized;
                float hdg = Mathf.Atan2(local.x, local.z);
                float pitch = Mathf.Asin(Mathf.Clamp(-local.y, -1f, 1f));
                if (Shots.SurfaceLike(arriveVessel)) pitch = Mathf.Min(pitch, 0.6f);
                pitch = Mathf.Clamp(pitch, fc.minPitch, fc.maxPitch);
                float h0 = fc.camHdg, p0 = fc.camPitch;
                FlightCamera.CamHdg = hdg;
                FlightCamera.CamPitch = pitch;
                Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "camera ends looking along the opening shot: heading {0:F0} -> {1:F0} deg, pitch {2:F0} -> {3:F0} deg",
                    h0 * Mathf.Rad2Deg, hdg * Mathf.Rad2Deg, p0 * Mathf.Rad2Deg, pitch * Mathf.Rad2Deg));
            }
            catch (Exception e) { Log.Warn("camera heading not set: " + e.Message); }
        }

        private CamPose ArrivePose(Vector3d anchor)
        {
            if (retStart < 0)
            {
                skyWeight = 1.0;
                return estPose(anchor);
            }
            double s = Overlay.Ease((float)((t - retStart) / retDur));
            skyWeight = 1.0 - s;
            if (arrivePlan != null)
            {
                CelestialBody b = arriveVessel.mainBody;
                return Paths.Around(retFrom, rig.Home(), anchor, b.position, b.Radius, s, arrivePlan, ZoomTo);
            }
            return ZoomTo(retFrom, rig.Home(), s);
        }

        /// <summary>
        /// Zoom between two poses around the anchor (log distance + FOV) that keeps the anchor on
        /// screen all the way: from its spot in the first shot to its spot in the second, the view's
        /// up turning from one to the other.
        /// </summary>
        private static CamPose ZoomTo(CamPose from, CamPose to, double s)
        {
            CamPose p = CamPose.Blend(from, to, s);
            Vector3 rayFrom = Quaternion.Inverse(from.Rot) * (Vector3)(-from.Off).normalized;
            if (rayFrom.z < 0.2f) return p;   // the anchor was behind the first shot's camera
            Vector3 rayTo = Quaternion.Inverse(to.Rot) * (Vector3)(-to.Off).normalized;
            Vector3 ray = Vector3.Slerp(rayFrom, rayTo, (float)s).normalized;
            Vector3 up = Vector3.Slerp(from.Rot * Vector3.up, to.Rot * Vector3.up, (float)s);
            Quaternion rot = ShotSolver.AimAnyRay(-p.Off, (Vector3d)ray, (Vector3d)up);
            if (s > 0.85) rot = Quaternion.Slerp(rot, to.Rot, Overlay.Ease((float)((s - 0.85) / 0.15)));
            return new CamPose(p.Off, rot, p.TanV);
        }

        private void ArriveApply(bool withOverlay)
        {
            Vessel v = arriveVessel;
            Vector3d anchor = rig.Anchor(v);
            CamPose p = ArrivePose(anchor);
            Vector3d cam = CameraRig.ClampAltitude(anchor + p.Off, anchor, v.mainBody);
            if (kind == Kind.Launch)
            {
                // the flight from the building's entrance stays above the ground; the floor lets go
                // as the zoom lands, so the hand-back pose is exact
                double s = retStart >= 0 ? Overlay.Ease((float)((t - retStart) / retDur)) : 0.0;
                if (s < 1.0) { Vector3d lifted = SoftGround(v.mainBody, cam, 3.0, 6.0); cam = lifted + (cam - lifted) * s; }
            }
            rig.Apply(cam, p.Rot, p.TanV, skyWeight);
            if (!withOverlay) return;

            // text and marker fade early in the zoom, the bars slide out as it lands
            double textEnd = retStart >= 0 ? retStart + Math.Min(1.5, 0.3 * retDur) : double.MaxValue;
            float textOut = (float)((textEnd - t) / 0.6);
            var f = new OverlayFrame
            {
                Bars = retStart >= 0 ? (float)((retStart + retDur - t) / Math.Min(1.2, retDur)) : 1f,
                LowerThird = true,
                FrozenAlpha = liveAt < 0 ? 1f   // the card covers the scene until it may be shown
                            : 1f - Overlay.Ease((float)((t - liveAt) / Mathf.Clamp(S.establishSeconds, 0.4f, 1.2f))),   // quick when the zoom starts at once
                FrozenZoom = 1f,
                FrozenPivot = new Vector2(0.5f, 0.5f),
                TitleAlpha = Mathf.Clamp01(Mathf.Min(1f, textOut)),
                DetailAlpha = Mathf.Clamp01(Mathf.Min(1f, textOut)),
                MarkerAlpha = Mathf.Clamp01(textOut),
            };
            if (S.showVesselMarker)
                f.MarkerOnScreen = CameraRig.ProjectMarker(cam, p.Rot, p.TanV, v.CoMD, arriveSize, v.mainBody, S.barFraction, out f.Marker);
            overlay.Apply(f);
        }

        private void EndArrive(string why)
        {
            if (phase != Phase.Arrive) return;
            Log.Info("vessel shot ended early: " + why);
            try { rig.HandBack(); } catch { }
            BeginReveal();
        }

        // ------------------------------------------------------------------ arrive (Space Center)

        /// <summary>
        /// The Space Center scene opens on a Kerbin shot (the frozen Tracking Station frame's own
        /// pose, carried across the load, or a fresh one with the Space Center on the visible side)
        /// and zooms down into the view the Space Center camera has set up (it ran, unlocked, while
        /// the card covered the screen). The flight camera is a child of that camera's transform,
        /// at zero offset: put back there at the end, the hand-over has no jump.
        /// </summary>
        private void BeginArriveKsc()
        {
            FlightCamera fc = FlightCamera.fetch;
            Transform ct = fc != null ? fc.transform.parent : null;
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (ct == null || home == null || ScaledCamera.Instance == null) { BeginReveal(); return; }

            kscScc = UnityEngine.Object.FindObjectOfType<SpaceCenterCamera2>();
            kscCt = ct;
            kscHomeTan = Math.Tan(fc.FieldOfView * 0.5 * Math.PI / 180.0);
            // the save card sits in the lower left corner: Kerbin on the right third, clear of it
            kscSx = lowerThird ? 1.0 - 2.0 * S.planetScreenX : 2.0 * S.planetScreenX - 1.0;
            Quaternion q = BodyRotation(home);
            if (carryValid && carryBody == home && !carryAerial)
            {
                kscOffB = carryOff;
                kscRotB = carryRot;
                kscTan = carryTanV;
            }
            else
            {
                Shot shot = ShotSolver.SolveFar(S, KscSite(home), home, Shots.SunDir(home), CameraRig.Aspect);
                Quaternion inv = Quaternion.Inverse(q);
                kscOffB = (Vector3d)(inv * (Vector3)shot.Offset);
                kscRotB = inv * shot.Rot;
                kscTan = shot.TanV;
            }
            carryValid = false;

            kscPlan = null;
            kscSettledAt = -1;
            kscFromBuilding = false;
            if (kind == Kind.EditorExit)
            {
                try
                {
                    if (FacadePose(exitFacility, home, (Vector3d)ct.position, out FacadeShot fs))
                    {
                        kscFacT = fs.Fac.facilityTransform;
                        kscFacPos = kscFacT.InverseTransformPoint((Vector3)fs.End);
                        kscFacRot = Quaternion.Inverse(kscFacT.rotation) * fs.Rot;
                        kscFacTan = fs.Tan;
                        kscFromBuilding = true;
                        Log.Info("arriving at the Space Center from outside the " + (exitFacility == EditorFacility.SPH ? "SPH" : "VAB") + " (" + fs.Describe + ")");
                    }
                }
                catch (Exception e) { Log.Warn("building view: " + e.Message); }
            }
            kscWarmDoneAt = S.warmUpSeconds > 0f && S.liveLoading ? -1 : 0;
            kscRig = new CameraRig(S, fc) { HandBackOnLeave = false };
            kscRig.TakenAway = () => EndKsc("camera taken");
            InputLockManager.SetControlLock(ControlTypes.All, LockId);   // the Space Center camera holds still
            HideHud();
            kscRig.Take();
            t = 0;
            retStart = -1;
            phase = Phase.ArriveKsc;
            KscPoses(home, out CamPose from, out CamPose to, out Vector3d kAnchor);
            Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "arriving at the Space Center from {0:F0} km, ending {1:F0} m above sea level, {2:F0} m from the camera pivot{3}; {4}",
                from.Off.magnitude / 1000.0, (kAnchor + to.Off - home.position).magnitude - home.Radius, to.Off.magnitude,
                sccSyncFailed || SccUpdateTransform == null ? " (camera not placed)" : "", overlay.Describe()));
            KscApply(true);
        }

        /// <summary>Opening shot and hand-over pose, both relative to the Space Center camera's
        /// pivot, from the live transforms (the planet turns under them while game time runs).</summary>
        private void KscPoses(CelestialBody home, out CamPose from, out CamPose to, out Vector3d anchor)
        {
            SyncKscCamera();
            if (kscFromBuilding && kscFacT != null)
            {
                anchor = kscScc != null ? (Vector3d)kscScc.transform.position : (Vector3d)kscCt.position;
                to = new CamPose((Vector3d)kscCt.position - anchor, kscCt.rotation, kscHomeTan);
                from = new CamPose((Vector3d)kscFacT.TransformPoint(kscFacPos) - anchor, kscFacT.rotation * kscFacRot, kscFacTan);
                return;
            }
            anchor = kscScc != null ? (Vector3d)kscScc.transform.position : (Vector3d)kscCt.position;
            to = new CamPose((Vector3d)kscCt.position - anchor, kscCt.rotation, kscHomeTan);
            Quaternion q = BodyRotation(home);
            var shot = new Shot { Mode = "ksc", Offset = (Vector3d)(q * (Vector3)kscOffB), Rot = q * kscRotB, TanV = kscTan };
            Vector3d site = KscSite(home);
            CamPose start = Shots.Reaim(shot, site, home, kscSx, shot.TanV);
            from = new CamPose(site + start.Off - anchor, start.Rot, start.TanV);
        }

        private static readonly System.Reflection.MethodInfo SccUpdateTransform =
            typeof(SpaceCenterCamera2).GetMethod("UpdateTransform", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        private bool sccSyncFailed;

        /// <summary>
        /// SpaceCenterCamera2 only places its transforms in Update, and Update returns early while
        /// camera controls are locked (our lock during the zoom): its CameraTransform stayed where
        /// the scene put it (at the pivot, near eye level), the zoom ended there, and the camera
        /// snapped to its real pose at the first input. Its own placement is run every frame
        /// instead (no input read; the flight camera is set after it).
        /// </summary>
        private void SyncKscCamera()
        {
            if (kscScc == null || SccUpdateTransform == null || sccSyncFailed) return;
            try { SccUpdateTransform.Invoke(kscScc, null); }
            catch (Exception e) { sccSyncFailed = true; Log.Warn("placing the Space Center camera: " + e.Message); }
        }

        /// <summary>The main menu's buttons are 3D text objects in the menu scene (TextProButton3D),
        /// not HUD canvases, so hiding the HUD left them over the card and in its frozen frame. The
        /// menu scene is about to be destroyed: its buttons, 3D texts and canvases are switched off.</summary>
        private static void HideMenu()
        {
            int n = 0;
            try
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (TextProButton3D b in root.GetComponentsInChildren<TextProButton3D>(true))
                        if (b != null && b.gameObject.activeSelf) { b.gameObject.SetActive(false); n++; }
                    foreach (TMPro.TextMeshPro tx in root.GetComponentsInChildren<TMPro.TextMeshPro>(false))
                        if (tx != null && tx.enabled) { tx.enabled = false; n++; }
                    foreach (Canvas cv in root.GetComponentsInChildren<Canvas>(false))
                        if (cv != null && cv.enabled) { cv.enabled = false; n++; }
                }
            }
            catch (Exception e) { Log.Warn("hiding the menu: " + e.Message); }
            Log.Info("main menu hidden (" + n + " objects)");
        }

        private bool musicWasPlaying, musicLogged;

        private static bool MusicPlaying()
        {
            try
            {
                MusicLogic m = MusicLogic.fetch;
                return m != null && ((m.audio1 != null && m.audio1.isPlaying) || (m.audio2 != null && m.audio2.isPlaying));
            }
            catch { return false; }
        }

        /// <summary>Logs once if the game's music stops while the card is up before the load (it
        /// should play on until the scene switch).</summary>
        private void WatchMusic()
        {
            if (musicLogged || !musicWasPlaying) return;
            if (MusicPlaying()) return;
            musicLogged = true;
            Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "the game's music stopped {0:F2} s into the card (time scale {1}, preloaded scene held: {2}, audio listener paused: {3})",
                Time.realtimeSinceStartup - clickAt, Time.timeScale, ScenePreload.Stalling, AudioListener.pause));
        }

        private void KscStep()
        {
            t += Math.Min(Time.unscaledDeltaTime, 1.0 / 30.0);
            if (kscRig == null || kscCt == null || kscRig.Fc != FlightCamera.fetch || HighLogic.LoadedScene != GameScenes.SPACECENTER)
            { EndKsc("camera or scene gone"); return; }
            KeepHudOff();
            double hold = kind == Kind.SaveLoad ? S.saveHoldSeconds : S.establishSeconds;
            // The frozen card stays until the scene has settled (clouds, terrain and textures
            // loaded: KSP's loading screen over, smooth frames, the log quiet); then it dissolves,
            // the shot holds, and the zoom starts.
            // First, under the card, the camera waits at the Space Center's own view until that
            // has settled (terrain, clouds, atmosphere built there: they popped in as the zoom
            // arrived); then it goes out to the planet shot, which settles in turn.
            if (kscWarmDoneAt < 0 && ((t >= S.warmUpSeconds && Settled()) || t > S.settleMaxSeconds))
            {
                kscWarmDoneAt = t;
                Milestone(t > S.settleMaxSeconds ? "Space Center view still busy, going on" : "Space Center view built");
            }
            if (kscWarmDoneAt >= 0 && kscSettledAt < 0 && t >= kscWarmDoneAt + 0.3
                && (Settled() || t > kscWarmDoneAt + S.settleMaxSeconds || !S.liveLoading))
            {
                kscSettledAt = t;
                Milestone(t > kscWarmDoneAt + S.settleMaxSeconds ? "scene still busy, going on" : "scene settled");
            }
            if (retStart < 0 && t > 0.5 && (Input.GetKeyDown(S.skipKey) || Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(0)))
            { if (kscWarmDoneAt < 0) kscWarmDoneAt = t; if (kscSettledAt < 0) kscSettledAt = t; retStart = t; retDur = Math.Min(S.zoomInSeconds, 2.0); PlanKsc(); }
            else if (retStart < 0 && kscSettledAt >= 0 && t >= kscSettledAt + hold && (smooth >= 10 || t > kscSettledAt + hold + 45.0 || !S.liveLoading))
            {
                // the shot holds while the game still stalls finishing its load
                Milestone("zoom down");
                retStart = t;
                retDur = S.zoomInSeconds;
                PlanKsc();
            }
            if (retStart >= 0 && t >= retStart + retDur)
            {
                ReleaseKsc();
                Cleanup();
                return;
            }
            KscApply(true);
        }

        private void PlanKsc()
        {
            kscPlan = null;
            if (kscFromBuilding) return;   // a short hop over the Space Center
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null || kscCt == null) return;
            KscPoses(home, out CamPose from, out CamPose to, out Vector3d anchor);
            kscPlan = Paths.Make(from, to, anchor, home.position, home.Radius);
            if (kscPlan != null)
                Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "the Space Center is behind {0}: zooming around it (in view at {1:F0} % of the zoom)", home.bodyName, kscPlan.Sw * 100.0));
        }

        private void KscApply(bool withOverlay)
        {
            if (kscRig == null || kscCt == null) return;
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null) return;
            double s = retStart >= 0 ? Overlay.Ease((float)((t - retStart) / retDur)) : 0.0;
            KscPoses(home, out CamPose from, out CamPose to, out Vector3d anchor);
            CamPose p = kscPlan != null ? Paths.Around(from, to, anchor, home.position, home.Radius, s, kscPlan, ZoomTo)
                                        : ZoomTo(from, to, s);
            Vector3d cam = anchor + p.Off;
            bool warming = kscWarmDoneAt < 0;
            if (warming)
                kscRig.Apply(anchor + to.Off, to.Rot, to.TanV, 0.0);   // hidden by the card
            else if (s < 1.0)
            {
                // never through the planet or the terrain; lets go at the end (exact hand-over)
                Vector3d lifted = SoftGround(home, OutsideSphere(cam, home.position, home.Radius + 50.0), 4.0, 8.0);
                cam = lifted + (cam - lifted) * (s * s);
            }
            if (!warming) kscRig.Apply(cam, p.Rot, p.TanV, 1.0 - s);
            KscTerrainSwitch(home, warming ? anchor + to.Off : cam);
            if (!withOverlay) return;

            double textEnd = retStart >= 0 ? retStart + Math.Min(1.5, 0.3 * retDur) : double.MaxValue;
            float textOut = Mathf.Clamp01((float)((textEnd - t) / 0.6));
            var f = new OverlayFrame
            {
                Bars = retStart >= 0 ? (float)((retStart + retDur - t) / Math.Min(1.2, retDur)) : 1f,
                LowerThird = lowerThird,
                // the frozen frame dissolves into the live shot once the scene has settled
                FrozenAlpha = kscSettledAt < 0 ? 1f : 1f - Overlay.Ease((float)((t - kscSettledAt) / 0.6)),
                FrozenZoom = 1f,
                FrozenPivot = new Vector2(0.5f, 0.5f),
                TitleAlpha = textOut,
                DetailAlpha = textOut,
                Line = textOut,
            };
            if (framedBody != null)
                f.PlanetOnScreen = CameraRig.ProjectSphere(cam, p.Rot, p.TanV, home.position, home.Radius, out f.Planet, out f.PlanetRadius);
            overlay.Apply(f);
        }

        // Space Center -> Tracking Station: the frozen camera, in the planet's own frame
        private bool kscFromBuilding;           // leaving the VAB / SPH: the arrival starts outside the building
        private Transform kscFacT;
        private Vector3 kscFacPos;
        private Quaternion kscFacRot;
        private double kscFacTan;

        private bool tsCarry;
        private Vector3d tsOffB;

        private void RecordTrackingPose()
        {
            tsCarry = false;
            if (kind != Kind.KscToTracking || travelBody == null) return;
            tsOffB = (Vector3d)(Quaternion.Inverse(BodyRotation(travelBody)) * (Vector3)(lastCam - travelBody.position));
            tsCarry = true;
        }

        /// <summary>
        /// The Tracking Station opens exactly on the frozen frame: its camera is set from the frozen
        /// camera position (PlanetariumCamera.SetCamCoordsFromPosition: heading, pitch and distance
        /// around the planet). Predicting its opening from its last heading and distance left the
        /// two views slightly apart. Runs every frame while the card covers the scene.
        /// </summary>
        private void MatchTrackingPose(bool log)
        {
            if (!tsCarry || HighLogic.LoadedScene != GameScenes.TRACKSTATION) return;
            try
            {
                PlanetariumCamera pc = PlanetariumCamera.fetch;
                CelestialBody home = FlightGlobals.GetHomeBody();
                if (pc == null || pc.target == null || home == null || home.scaledBody == null) return;
                if (pc.target.celestialBody != home) return;
                Vector3 rel = BodyRotation(home) * (Vector3)tsOffB;
                Vector3 scaledPos = home.scaledBody.transform.position + rel * ScaledSpace.InverseScaleFactor;
                float h0 = pc.camHdg, p0 = pc.camPitch, d0 = pc.Distance;
                pc.SetCamCoordsFromPosition(scaledPos);
                if (log)
                    Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "the Tracking Station opens on the frozen frame: heading {0:F1} -> {1:F1} deg, pitch {2:F1} -> {3:F1} deg, distance {4:F0} -> {5:F0} km",
                        h0 * Mathf.Rad2Deg, pc.camHdg * Mathf.Rad2Deg, p0 * Mathf.Rad2Deg, pc.camPitch * Mathf.Rad2Deg,
                        d0 * ScaledSpace.ScaleFactor / 1000.0, pc.Distance * ScaledSpace.ScaleFactor / 1000.0));
            }
            catch (Exception e) { tsCarry = false; Log.Warn("matching the Tracking Station view: " + e.Message); }
        }

        /// <summary>Seamless has the home planet's terrain switched off in the Space Center scene
        /// (a far shot there): EVE's cloud update is then run as in flight (HarmonyHook).</summary>
        internal static bool SpaceCenterTerrainOff
        {
            get
            {
                Transitions t = instance;
                return t != null && t.kscPqs != null && !t.kscPqs.isActive;
            }
        }

        private PQS kscPqs;
        private double kscPqsOffAbove;
        private bool kscPqsLogged;

        /// <summary>
        /// In flight the planet's terrain system switches off above its deactivation altitude (by the
        /// higher of the craft and the flight camera: PSystemManager makes the camera every PQS's
        /// secondary target) and Scatterer and EVE draw the atmosphere and clouds in their
        /// from-space mode. The Space Center scene never does that (KSP skips the check without an
        /// active vessel), so far shots there kept the close-up mode: no atmosphere or clouds on the
        /// planet shot, unbuilt terrain on the far side. Every camera move in the Space Center scene
        /// (arrivals and departures) switches the terrain the way flight does, by the camera's
        /// altitude (switching off keeps the built terrain); it is on again before the next scene
        /// loads and whenever a transition ends.
        /// </summary>
        private void KscTerrainSwitch(CelestialBody home, Vector3d cam)
        {
            try
            {
                if (kscPqs == null)
                {
                    kscPqs = home.pqsController;
                    if (kscPqs == null) return;
                    kscPqsOffAbove = 0.0;
                    foreach (var m in kscPqs.GetComponentsInChildren<PQSMod_CelestialBodyTransform>(true))
                        if (m != null && m.deactivateAltitude > 0.0) { kscPqsOffAbove = m.deactivateAltitude; break; }
                    if (kscPqsOffAbove <= 0.0) kscPqsOffAbove = Math.Max(90000.0, 0.25 * home.Radius);
                    kscPqsLogged = false;
                }
                double alt = (cam - home.position).magnitude - home.Radius;
                if (kscPqs.isActive && alt > kscPqsOffAbove * 1.05)
                {
                    kscPqs.DeactivateSphere();
                    if (!kscPqsLogged)
                    {
                        kscPqsLogged = true;
                        Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "{0}'s terrain off above {1:F0} km while the camera is up (atmosphere and clouds from space, as in flight)",
                            home.bodyName, kscPqsOffAbove / 1000.0));
                    }
                }
                else if (!kscPqs.isActive && alt < kscPqsOffAbove)
                    kscPqs.ActivateSphere();
            }
            catch (Exception e) { Log.Warn("terrain switch: " + e.Message); kscPqs = null; }
        }

        /// <summary>The terrain is on again whatever happened (the Space Center needs it).</summary>
        private void KscTerrainOn()
        {
            try { if (kscPqs != null && !kscPqs.isActive) kscPqs.ActivateSphere(); } catch { }
            kscPqs = null;
        }

        /// <summary>Puts the flight camera back under the Space Center camera (zero offset) and
        /// restores its field of view, the scaled camera link and the skybox.</summary>
        private void ReleaseKsc()
        {
            KscTerrainOn();
            CameraRig r = kscRig;
            kscRig = null;
            kscCt = null;
            kscScc = null;
            if (r == null) return;
            try { r.Release(); } catch { }
            try
            {
                if (r.Fc != null && r.Fc.transform.parent != null)
                {
                    r.Fc.transform.localPosition = Vector3.zero;
                    r.Fc.transform.localRotation = Quaternion.identity;
                }
            }
            catch { }
        }

        private void EndKsc(string why)
        {
            if (phase != Phase.ArriveKsc) return;
            Log.Info("Space Center shot ended early: " + why);
            ReleaseKsc();
            Cleanup();
        }

        // ------------------------------------------------------------------ live loading

        private void Milestone(string what)
        {
            float now = Time.realtimeSinceStartup;
            Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}: {1:F1} s after the load started ({2:F1} s after the click)", what, now - loadAt, now - clickAt));
        }

        /// <summary>KSP's loading screen renders nothing while our card covers the scene. Its camera
        /// stays enabled / disabled on KSP's own schedule, so its end can still be seen.</summary>
        private static void SuppressMask()
        {
            if (maskSuppressed) return;
            try
            {
                var m = MaskInstanceField != null ? MaskInstanceField.GetValue(null) as LoadingBufferMask : null;
                if (m == null || m.camera == null) return;
                maskCam = m.camera;
                maskCull = maskCam.cullingMask;
                maskClear = maskCam.clearFlags;
                maskCam.cullingMask = 0;
                maskCam.clearFlags = CameraClearFlags.Nothing;
                maskSuppressed = true;
            }
            catch (Exception e) { Log.Warn("loading screen: " + e.Message); }
        }

        private static void RestoreMask()
        {
            if (!maskSuppressed) return;
            maskSuppressed = false;
            try
            {
                if (maskCam != null) { maskCam.cullingMask = maskCull; maskCam.clearFlags = maskClear; }
            }
            catch { }
            maskCam = null;
        }

        /// <summary>Once KSP hides its loading screen: put it back to normal, and report when that
        /// was (the time a transition saved against KSP's own loading screen).</summary>
        private void WatchStockMask()
        {
            if (!maskSuppressed && !stockEndPending) return;
            if (phase != Phase.Idle && phase != Phase.Arrive && phase != Phase.ArriveKsc && phase != Phase.Reveal) return;
            bool hidden;
            try { hidden = maskCam == null || !maskCam.enabled; } catch { hidden = true; }
            if (!hidden) return;
            if (stockEndPending || measuring)
                Milestone(measuring ? "KSP's own loading screen ended (transition still running)" : "KSP's own loading screen ended");
            stockEndPending = false;
            RestoreMask();
        }

        // ------------------------------------------------------------------ reveal / cleanup

        private void BeginReveal()
        {
            MatchTrackingPose(true);
            tsCarry = false;
            HideHud();
            // The Tracking Station keeps its orbit lines: they dissolve in with the scene (switched
            // off, they could only pop in at the end).
            if (targetScene != GameScenes.TRACKSTATION) HideOrbits();
            phase = Phase.Reveal;
            t = 0;
        }

        private void RevealFrame()
        {
            if (t < 1.5) KeepHudOff();
            float frozenA = 1f - Overlay.Ease((float)((t - 0.3) / 1.2));
            float textOut = frozenA;   // the frozen frame holds a copy of the card: fade them together
            var f = new OverlayFrame
            {
                Bars = 1f - Overlay.Ease((float)((t - 0.6) / 1.2)),
                FrozenAlpha = frozenA,
                FrozenZoom = frozenZoom,
                LowerThird = lowerThird || !frozenPlanetOnScreen,
                TitleAlpha = Mathf.Clamp01(textOut),
                DetailAlpha = Mathf.Clamp01(textOut),
                Line = Mathf.Clamp01(textOut),
            };
            if (frozenPlanetOnScreen)
            {
                f.FrozenPivot = new Vector2(frozenPlanet.x / Screen.width, frozenPlanet.y / Screen.height);
                f.PlanetOnScreen = true;
                f.Planet = frozenPlanet;
                f.PlanetRadius = frozenPlanetRadius * frozenZoom;
            }
            overlay.Apply(f);
            if (t >= 1.9) Cleanup();
        }

        private void Unpause()
        {
            if (!pausedByUs) return;
            pausedByUs = false;
            Time.timeScale = timeScaleBefore > 0f ? timeScaleBefore : 1f;   // never carry a stopped clock on
        }

        /// <summary>
        /// Stops game time while the camera flies (the planets stay where they are at the click,
        /// which is the moment the new scene starts from) WITHOUT KSP's pause: that fires
        /// onGamePause, which stops the music (stock MusicLogic, soundtrack mods). Time.timeScale = 0
        /// freezes physics, orbits and the universal time just the same. Coming from the pause
        /// menu (Space Center button there), the game is unpaused first so the music resumes.
        /// </summary>
        private void FreezeTime()
        {
            try { if (FlightDriver.Pause) FlightDriver.SetPause(false, false); } catch { }
            InputLockManager.RemoveControlLock("gamePause");
            timeScaleBefore = Time.timeScale > 0f ? Time.timeScale : 1f;
            Time.timeScale = 0f;
            pausedByUs = true;
            HideHud();   // SetPause fires onShowUI
        }

        /// <summary>The whole HUD off while the bars are up: the event (mods' windows) and the
        /// UI canvases directly (KSP's loading mask shows them directly, bypassing the event's flag).</summary>
        private void HideHud()
        {
            try
            {
                GameEvents.onHideUI.Fire();
                if (UIMasterController.Instance != null) UIMasterController.Instance.HideUI();
            }
            catch (Exception e) { Log.Warn("hide HUD: " + e.Message); }
            uiHidden = true;
        }

        /// <summary>Re-hide the canvases every frame while the card covers the scene: the game's
        /// loading mask re-shows them (ShowUI at the end of its show duration), bypassing the event.</summary>
        private void KeepHudOff()
        {
            try
            {
                // only when something showed it again (HideUI logs a line every time it runs)
                UIMasterController ui = UIMasterController.Instance;
                if (ui != null && ((ui.uiCamera != null && ui.uiCamera.enabled) || (ui.mainCanvas != null && ui.mainCanvas.enabled)
                                   || (ui.appCanvas != null && ui.appCanvas.enabled)))
                    ui.HideUI();
            }
            catch { }
            uiHidden = true;
        }

        private void Fail()
        {
            Unpause();
            Phase was = phase;
            try { if (phase == Phase.Arrive && rig != null) rig.HandBack(); } catch { }
            try { if (phase == Phase.ArriveKsc) ReleaseKsc(); } catch { }
            try { if (drv != null && (was == Phase.Depart || was == Phase.Capture)) drv.Leave(); } catch { }
            try { if (reenableOnFail != null) reenableOnFail.enabled = true; } catch { }
            reenableOnFail = null;
            Cleanup();
            if (!loadIssued && (was == Phase.Depart || was == Phase.Capture || was == Phase.PendingLoad))
            {
                loadIssued = true;
                bypass = true;
                HighLogic.LoadScene(targetScene);
            }
        }

        private void Cleanup()
        {
            if (measuring)
            {
                measuring = false;
                Milestone(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "transition done ({0} frames over 0.1 s, longest {1:F1} s)", stallFrames, maxStall));
                if (!MaskHidden()) stockEndPending = true;
                else Log.Info("KSP's own loading screen had already ended");
            }
            phase = Phase.Idle;
            KscTerrainOn();
            try { if (overlay != null) overlay.Destroy(); } catch { }
            overlay = null;
            RestoreOrbits();   // back to normal once the reveal is over
            InputLockManager.RemoveControlLock(LockId);
            Unpause();
            if (uiHidden)
            {
                try
                {
                    GameEvents.onShowUI.Fire();
                    if (UIMasterController.Instance != null) UIMasterController.Instance.ShowUI();
                }
                catch { }
            }
            uiHidden = false;
            sceneHudHidden = false;
            rig = null;
            kscRig = null;
            drv = null;
            departAnchor = null;
            departAlive = null;
            travelCenter = null;
            noRise = false;
            riseRequired = false;
            staticDepart = false;
            departVessel = null;
            arriveVessel = null;
            switchVessel = null;
            departTarget = estPose = null;
        }

        /// <summary>Keeps every loaded vessel on rails while the camera is far away: KSP switches
        /// the terrain (and its colliders) off when the camera is high above it.</summary>
        private static void HoldVessels()
        {
            try
            {
                var loaded = FlightGlobals.VesselsLoaded;
                for (int i = 0; i < loaded.Count; i++)
                    if (loaded[i] != null && !loaded[i].packed) loaded[i].GoOnRails();
                OrbitPhysicsManager.HoldVesselUnpack(10);
            }
            catch { }
        }
    }
}
