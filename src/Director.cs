using System.Collections.Generic;
using UnityEngine;

namespace Seamless
{
    /// <summary>
    /// Watches the active vessel and starts the shot when it has just been captured by a body
    /// it has never been at before:
    ///   - closed orbit inside the body's SOI (no escape), periapsis above the surface,
    ///   - out of the atmosphere, orbiting or sub-orbital (aerocaptures count),
    ///   - no engine has produced thrust for quietSeconds,
    ///   - normal flight camera, no map view, not paused, no time warp.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class Director : MonoBehaviour
    {
        private static Director instance;
        private Settings S;
        private Cinematic active;

        /// <summary>Ends a running arrival shot (camera handed back) - used before a scene transition.</summary>
        public static void AbortActive(string why)
        {
            if (instance != null && instance.active != null)
            {
                instance.active.Abort(why);
                instance.active = null;
            }
        }
        private Vessel watched;
        private CelestialBody watchedBody;
        private float quiet;
        private float settled;     // time since the last SOI change (EVE, Scatterer load the body then)
        private float offRails;    // time since the craft came off rails (time warp ended)
        private const float SettleSeconds = 5f;
        private const float OffRailsSeconds = 1f;
        private List<ModuleEngines> engines = new List<ModuleEngines>();
        private float engineScanAge = 999f;
        private float lastNote = -999f;

        private void Awake()
        {
            instance = this;
            S = Settings.Load();
            GameEvents.onVesselWasModified.Add(OnVesselModified);
            Log.Info("v" + AssemblyInfo.Version + " ready" + (S.enabled ? "" : " (disabled in settings.cfg)")
                     + (S.previewKey != KeyCode.None ? ", preview key " + S.previewKey : ""));
        }

        private void OnDestroy()
        {
            GameEvents.onVesselWasModified.Remove(OnVesselModified);
            if (instance == this) instance = null;
            if (active != null) { active.Abort("scene ended"); active = null; }
        }

        private void OnVesselModified(Vessel v)
        {
            if (v != null && v == FlightGlobals.ActiveVessel) engineScanAge = 999f;
        }

        private void Update()
        {
            if (active != null)
            {
                active.Update();
                if (active.Done) active = null;
                return;
            }
            if (!S.enabled || !FlightGlobals.ready) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody == null) return;

            if (v != watched) { watched = v; quiet = 0f; settled = 0f; offRails = 0f; engineScanAge = 999f; }
            // Separately from the engines-off wait, the shot never starts within SettleSeconds of an
            // SOI change (EVE / Scatterer load the new body then, which stalls frames) or within
            // OffRailsSeconds of time warp ending (a pack / unpack moves things). It was 5 s after
            // either: a short insertion burn right after warping to the node waited ~5 s.
            if (v.mainBody != watchedBody) { watchedBody = v.mainBody; quiet = 0f; settled = 0f; }
            settled += Time.deltaTime;
            if (v.packed) offRails = 0f; else offRails += Time.deltaTime;
            engineScanAge += Time.deltaTime;
            if (engineScanAge > 2f)
            {
                engines = v.FindPartModulesImplementing<ModuleEngines>() ?? new List<ModuleEngines>();
                engineScanAge = 0f;
            }
            if (AnyEngineThrusting()) quiet = 0f; else if (!v.packed) quiet += Time.deltaTime;

            if (S.previewKey != KeyCode.None && Input.GetKeyDown(S.previewKey))
            {
                string why = CameraBusy(v, false);
                if (why == null) active = Cinematic.Begin(S, v, true);
                else Log.Info("preview not possible: " + why);
                return;
            }

            var tracker = v.FindVesselModuleImplementing<ArrivalTracker>();
            if (tracker == null) return;
            tracker.EnsureSeeded();

            CelestialBody b = v.mainBody;
            if (quiet < S.quietSeconds || settled < SettleSeconds || offRails < OffRailsSeconds) return;
            if (tracker.HasVisited(b)) return;
            if (b.isStar && !S.includeStars) return;
            if (!Arrived(v, b)) return;
            string busy = CameraBusy(v, true);
            if (busy != null)
            {
                if (S.debugLog && Time.unscaledTime - lastNote > 10f)
                {
                    lastNote = Time.unscaledTime;
                    Log.Info("arrived at " + b.bodyName + ", waiting: " + busy);
                }
                return;
            }

            tracker.MarkVisited(b);
            active = Cinematic.Begin(S, v, false);
        }

        private void LateUpdate()
        {
            if (active != null)
            {
                active.LateUpdate();
                if (active.Done) active = null;
            }
        }

        private static bool Arrived(Vessel v, CelestialBody b)
        {
            if (v.situation != Vessel.Situations.ORBITING && v.situation != Vessel.Situations.SUB_ORBITAL) return false;
            if (b.atmosphere && v.altitude < b.atmosphereDepth) return false;
            Orbit o = v.orbit;
            if (!Arrival.IsCaptured(o, b)) return false;
            return o.PeR > b.Radius;
        }

        private string CameraBusy(Vessel v, bool forTrigger)
        {
            FlightCamera fc = FlightCamera.fetch;
            if (fc == null) return "no flight camera";
            if (!fc.updateActive) return "another mod is driving the camera";
            if (fc.Target == null) return "camera has no target";
            if (MapView.MapIsEnabled) return "map view";
            if (CameraManager.Instance == null || CameraManager.Instance.currentCameraMode != CameraManager.CameraMode.Flight) return "not in the flight camera";
            if (FlightDriver.Pause) return "paused";
            if (!v.loaded) return "vessel not loaded";
            if (forTrigger && TimeWarp.CurrentRate > 1.01f) return "time warp";
            return null;
        }

        private bool AnyEngineThrusting()
        {
            for (int i = 0; i < engines.Count; i++)
            {
                ModuleEngines e = engines[i];
                if (e == null || !e.EngineIgnited || !e.isOperational) continue;
                if (e.finalThrust > 0.001f || e.currentThrottle > 0.001f) return true;
            }
            return false;
        }
    }
}
