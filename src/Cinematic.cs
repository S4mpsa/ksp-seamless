using System;
using KSP.UI;
using UnityEngine;

namespace Seamless
{
    /// <summary>
    /// One running arrival shot (camera via <see cref="CameraRig"/>).
    ///
    /// Timeline (t = real seconds since start):
    ///   0 .. A          camera moves from where it was to the shot (log distance, slerped direction)
    ///   A-0.4 .. A+1.3  leader line draws, title and details fade in
    ///   A .. F          hold, with a slow push-in
    ///   F .. F+0.7      text fades, line retracts (F = A + hold, or the moment the shot is skipped)
    ///   F+0.5 .. +ret   camera returns to the normal flight camera; bars slide out at the end
    /// </summary>
    internal sealed class Cinematic
    {
        private const string LockId = "Seamless";

        private readonly Settings S;
        private readonly Vessel vessel;
        private readonly CelestialBody body;
        private readonly CameraRig rig;
        private readonly Shot shot;
        private readonly Overlay overlay;
        private readonly double sx;
        private readonly double vesselSize;
        private readonly CamPose start;

        private double t;
        private double fadeOut;            // text fade-out start
        private double returnStart = -1;   // camera return start
        private double returnDuration;
        private CamPose retFrom;
        private Paths.Plan approachPlan, returnPlan;   // the planet between the shot and the vessel
        private double shotWeight;

        private readonly bool uiWasShowing;
        private bool finished;

        public bool Done { get { return finished; } }

        public static Cinematic Begin(Settings s, Vessel v, bool preview)
        {
            FlightCamera fc = FlightCamera.fetch;
            if (fc == null || fc.GetPivot() == null || v == null || v.mainBody == null) return null;
            try
            {
                return new Cinematic(s, v, fc, preview);
            }
            catch (Exception e)
            {
                Log.Warn("could not start: " + e);
                try { fc.ActivateUpdate(); } catch { }
                InputLockManager.RemoveControlLock(LockId);
                return null;
            }
        }

        private Cinematic(Settings s, Vessel v, FlightCamera fc, bool preview)
        {
            S = s;
            vessel = v;
            body = v.mainBody;
            sx = 2.0 * s.planetScreenX - 1.0;
            rig = new CameraRig(s, fc);
            rig.TakenAway = () => Abort("camera retargeted");

            vesselSize = Shots.VesselSize(v);
            Vector3d anchor = rig.Anchor(v);
            shot = ShotSolver.Solve(s, anchor, body, Shots.SunDir(body), vesselSize, CameraRig.Aspect);
            start = rig.Current(anchor);
            fadeOut = s.approachSeconds + s.holdSeconds;
            approachPlan = Paths.Make(ShotPose(), start, anchor, body.position, body.Radius);

            string titleText = ArrivalText.Title(body);
            string detailText = ArrivalText.Details(v, body, s);
            overlay = new Overlay(s, titleText, detailText, v.vesselName);

            Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0} {1} at {2} ({3}): {4} shot, fov {5:F2} deg, camera {6:F0} km from the centre, {7:F0} m from the vessel; vessel size {8:F1} m",
                preview ? "preview" : "arrival", v.vesselName, body.bodyName, detailText.Split('\n')[0],
                shot.Mode, 2.0 * Math.Atan(shot.TanV) * 180.0 / Math.PI, shot.Distance / 1000.0, shot.VesselDistance, vesselSize));
            if (S.debugLog)
            {
                CamPose home = rig.Home();
                Log.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "home pose check: predicted vs actual {0:F2} m, {1:F2} deg (mode {2}, FoR {3})",
                    (home.Off - start.Off).magnitude, Quaternion.Angle(home.Rot, start.Rot), fc.mode, fc.FoRMode));
            }

            rig.Take();
            InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS | ControlTypes.CAMERAMODES | ControlTypes.STAGING | ControlTypes.PAUSE, LockId);
            uiWasShowing = UIMasterController.Instance != null && UIMasterController.Instance.IsUIShowing;
            if (uiWasShowing) GameEvents.onHideUI.Fire();
        }

        // ------------------------------------------------------------------ per frame

        public void Update()
        {
            if (finished) return;
            string why = Interrupted();
            if (why != null) { Abort(why); return; }

            // A long frame (asset loading, PQS rebuilds) slows the move down instead of making it jump.
            if (!FlightDriver.Pause) t += Math.Min(Time.unscaledDeltaTime, 1.0 / 30.0);

            if (returnStart < 0 && (Input.GetKeyDown(S.skipKey) || Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(0)))
            {
                if (t < fadeOut) fadeOut = t;
                returnDuration = Math.Min(S.returnSeconds, 2.0);
            }

            Frame();
        }

        public void LateUpdate()
        {
            if (finished) return;
            Frame();   // again after physics/other LateUpdates may have moved things
        }

        private void Frame()
        {
            if (returnStart < 0 && t >= fadeOut + 0.5)
            {
                retFrom = Pose();
                returnPlan = Paths.Make(retFrom, rig.Home(), rig.Anchor(vessel), body.position, body.Radius);
                returnStart = t;
                if (returnDuration <= 0) returnDuration = S.returnSeconds;
            }
            if (returnStart >= 0 && t >= returnStart + returnDuration)
            {
                Finish();
                return;
            }

            CamPose p = Pose();
            Vector3d anchor = rig.Anchor(vessel);
            Vector3d cam = CameraRig.ClampAltitude(anchor + p.Off, anchor, body);
            rig.Apply(cam, p.Rot, p.TanV, shotWeight);
            overlay.Apply(OverlayState(cam, p.Rot, p.TanV));
        }

        /// <summary>The camera pose for the current time, relative to the anchor.</summary>
        private CamPose Pose()
        {
            if (returnStart >= 0)
            {
                double s = Overlay.Ease((float)((t - returnStart) / returnDuration));
                shotWeight = 1.0 - s;
                if (returnPlan != null)
                    return Paths.Around(retFrom, rig.Home(), rig.Anchor(vessel), body.position, body.Radius, s, returnPlan, CamPose.Blend);
                return CamPose.Blend(retFrom, rig.Home(), s);
            }
            CamPose target = ShotPose();
            double a = Overlay.Ease((float)(t / S.approachSeconds));
            shotWeight = a;
            if (a >= 1) return target;
            // around the planet when it hides the vessel from the shot: the zoom out, played backwards
            if (approachPlan != null)
                return Paths.Around(target, start, rig.Anchor(vessel), body.position, body.Radius, 1.0 - a, approachPlan, CamPose.Blend);
            return CamPose.Blend(start, target, a);
        }

        /// <summary>The solved shot, kept moving with the vessel and re-aimed so the planet stays put.</summary>
        private CamPose ShotPose()
        {
            double hold = Math.Max(0.001, fadeOut + 0.5 - S.approachSeconds * 0.5);
            double u = Overlay.Ease((float)((t - S.approachSeconds * 0.5) / hold));
            double tanV = shot.TanV * (1.0 - S.pushIn * u);
            return Shots.Reaim(shot, rig.Anchor(vessel), body, sx, tanV);
        }

        // ------------------------------------------------------------------ overlay state

        private OverlayFrame OverlayState(Vector3d cam, Quaternion rot, double tanV)
        {
            var f = new OverlayFrame();
            double A = S.approachSeconds;
            float barsIn = (float)(t / 1.2);
            float barsOut = returnStart >= 0 ? (float)((returnStart + returnDuration - t) / Math.Min(1.2, returnDuration)) : 1f;
            f.Bars = Mathf.Min(barsIn, barsOut);

            float lineIn = (float)((t - (A - 0.4)) / 1.0);
            float lineOut = (float)((fadeOut + 0.7 - t) / 0.5);
            f.Line = Mathf.Clamp01(Mathf.Min(lineIn, lineOut));
            float textOut = (float)((fadeOut + 0.6 - t) / 0.6);
            f.TitleAlpha = Mathf.Clamp01(Mathf.Min((float)((t - (A + 0.1)) / 0.8), textOut));
            f.DetailAlpha = Mathf.Clamp01(Mathf.Min((float)((t - (A + 0.5)) / 0.8), textOut));
            f.MarkerAlpha = Mathf.Clamp01(Mathf.Min((float)((t - (A + 0.8)) / 0.8), textOut));

            f.PlanetOnScreen = CameraRig.ProjectSphere(cam, rot, tanV, body.position, body.Radius, out f.Planet, out f.PlanetRadius);
            if (S.showVesselMarker)
            {
                f.MarkerOnScreen = CameraRig.ProjectMarker(cam, rot, tanV, vessel.CoMD, vesselSize, body, S.barFraction, out f.Marker, out float fade);
                f.MarkerAlpha *= fade;
            }
            return f;
        }

        // ------------------------------------------------------------------ ending

        private string Interrupted()
        {
            if (rig.Fc == null || FlightCamera.fetch != rig.Fc) return "flight camera gone";
            if (vessel == null || FlightGlobals.ActiveVessel != vessel) return "vessel switched";
            if (vessel.mainBody != body) return "SOI changed";
            if (MapView.MapIsEnabled) return "map view";
            if (CameraManager.Instance != null && CameraManager.Instance.currentCameraMode != CameraManager.CameraMode.Flight) return "camera mode changed";
            return null;
        }

        public void Abort(string why)
        {
            if (finished) return;
            Log.Info("shot ended early: " + why);
            Finish();
        }

        private void Finish()
        {
            if (finished) return;
            finished = true;
            rig.HandBack();
            InputLockManager.RemoveControlLock(LockId);
            try { overlay.Destroy(); } catch { }
            if (uiWasShowing && UIMasterController.Instance != null && !UIMasterController.Instance.IsUIShowing)
                GameEvents.onShowUI.Fire();
        }
    }
}
