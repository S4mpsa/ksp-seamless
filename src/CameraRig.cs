using System;
using System.Reflection;
using UnityEngine;

namespace Seamless
{
    /// <summary>A camera pose relative to the anchor (the flight camera pivot).</summary>
    internal struct CamPose
    {
        public Vector3d Off;
        public Quaternion Rot;
        public double TanV;   // tan(vertical FOV / 2)

        public CamPose(Vector3d off, Quaternion rot, double tanV) { Off = off; Rot = rot; TanV = tanV; }

        /// <summary>Log distance, slerped direction / rotation, log tan(FOV): smooth over huge ranges.</summary>
        public static CamPose Blend(CamPose a, CamPose b, double s)
        {
            double da = Math.Max(0.5, a.Off.magnitude), db = Math.Max(0.5, b.Off.magnitude);
            double dist = Math.Exp(Lerp(Math.Log(da), Math.Log(db), s));
            Vector3 dir = Vector3.Slerp((Vector3)(a.Off / da), (Vector3)(b.Off / db), (float)s);
            return new CamPose((Vector3d)dir.normalized * dist,
                               Quaternion.Slerp(a.Rot, b.Rot, (float)s),
                               Math.Exp(Lerp(Math.Log(a.TanV), Math.Log(b.TanV), s)));
        }

        private static double Lerp(double a, double b, double s) { return a + (b - a) * s; }
    }

    /// <summary>
    /// Takes the flight camera over (FlightCamera.DeactivateUpdate, then its transform, FOV and the
    /// scaled-space camera are set every frame) and hands it back at the exact pose FlightCamera
    /// computes itself when it resumes, so there is no jump. Also dims the skybox while a shot runs.
    /// </summary>
    internal sealed class CameraRig : ICamDriver
    {
        private static readonly FieldInfo OffsetHdgField = typeof(FlightCamera).GetField("offsetHdg", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo OffsetPitchField = typeof(FlightCamera).GetField("offsetPitch", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        public readonly FlightCamera Fc;
        public readonly float OrigFov;
        private readonly Settings S;
        private readonly GalaxyCubeControl galaxy;
        private readonly Color galaxyMax;
        private Callback prevAbort;
        private Transform prevScaledTarget;
        private bool taken, released;

        /// <summary>Called when the game retargets the camera (vessel switch, scene change).</summary>
        public Action TakenAway { get; set; }

        /// <summary>Leave(): true (flight) = hand the camera back to FlightCamera; false (Space
        /// Center, where SpaceCenterCamera2 drives it with FlightCamera updates off) = only restore.</summary>
        public bool HandBackOnLeave = true;

        public void Leave()
        {
            if (HandBackOnLeave) HandBack();
            else Release();
        }

        /// <summary>Restores FOV, scaled-camera link and skybox without touching FlightCamera's control.</summary>
        public void Release()
        {
            if (released) return;
            released = true;
            try
            {
                if (Fc != null)
                {
                    if (Fc.AbortExternalControl == (Callback)OnAbort) Fc.AbortExternalControl = prevAbort;
                    Fc.SetFoV(OrigFov);
                }
            }
            catch (Exception e) { Log.Warn("camera release: " + e.Message); }
            try { if (taken) RestoreScaledTarget(); } catch { }
            RestoreSky();
        }

        public CameraRig(Settings s, FlightCamera fc)
        {
            S = s;
            Fc = fc;
            OrigFov = fc.FieldOfView;
            galaxy = GalaxyCubeControl.Instance;
            if (galaxy != null) galaxyMax = galaxy.maxGalaxyColor;
        }

        public static double Aspect { get { return (double)Screen.width / Math.Max(1, Screen.height); } }

        public void Take()
        {
            if (taken) return;
            taken = true;
            Fc.DeactivateUpdate();
            prevAbort = Fc.AbortExternalControl;
            Fc.AbortExternalControl = OnAbort;
            // ScaledCamera normally copies the flight camera's (float) position every LateUpdate;
            // far from the vessel that is only good to ~1 km. Apply sets it from doubles instead.
            ScaledCamera sc = ScaledCamera.Instance;
            if (sc != null) { prevScaledTarget = sc.tgtRef; sc.tgtRef = null; }
        }

        /// <summary>Takes the camera again after the game took it away (a re-target while a scene is
        /// still loading); the original FOV, scaled-camera link and skybox colour are kept.</summary>
        public void Retake()
        {
            taken = false;
            released = false;
            Take();
        }

        private void RestoreScaledTarget()
        {
            ScaledCamera sc = ScaledCamera.Instance;
            if (sc != null && sc.tgtRef == null)
                sc.tgtRef = prevScaledTarget != null ? prevScaledTarget : (Fc != null ? Fc.transform : null);
        }

        private void OnAbort()
        {
            Callback p = prevAbort;
            if (TakenAway != null) TakenAway();
            if (p != null) p();
        }

        public Vector3d Anchor(Vessel fallback)
        {
            Transform p = Fc.GetPivot();
            if (p != null) return (Vector3d)p.position;
            return fallback != null ? fallback.CoMD : Vector3d.zero;
        }

        /// <summary>Where the camera is now.</summary>
        public CamPose Current(Vector3d anchor)
        {
            float fov = Fc.mainCamera != null ? Fc.mainCamera.fieldOfView : OrigFov;
            return new CamPose((Vector3d)Fc.transform.position - anchor, Fc.transform.rotation, Math.Tan(fov * 0.5 * Math.PI / 180.0));
        }

        /// <summary>Where FlightCamera will put the camera when it resumes (UpdateCameraTransform).</summary>
        public CamPose Home()
        {
            Quaternion pr = PivotRotation();
            Quaternion rot = pr;
            float oh = ReadFloat(OffsetHdgField), op = ReadFloat(OffsetPitchField);
            if (oh != 0f) rot = Quaternion.AngleAxis(oh * Mathf.Rad2Deg, pr * Vector3.up) * rot;
            if (op != 0f) rot = rot * Quaternion.AngleAxis(op * Mathf.Rad2Deg, Vector3.right);
            return new CamPose((Vector3d)(pr * (Vector3.back * Fc.Distance)), rot, Math.Tan(OrigFov * 0.5 * Math.PI / 180.0));
        }

        private Quaternion PivotRotation()
        {
            Quaternion frame = FlightGlobals.GetFoR(Fc.FoRMode);
            return frame * Quaternion.AngleAxis(Fc.camHdg * Mathf.Rad2Deg, Vector3.up)
                         * Quaternion.AngleAxis(Fc.camPitch * Mathf.Rad2Deg, Vector3.right);
        }

        private float ReadFloat(FieldInfo f)
        {
            if (f == null) return 0f;
            try { return (float)f.GetValue(Fc); } catch { return 0f; }
        }

        /// <summary>Keeps a moving camera above the halfway height between surface and anchor.</summary>
        public static Vector3d ClampAltitude(Vector3d cam, Vector3d anchor, CelestialBody body)
        {
            Vector3d P = body.position;
            double rv = (anchor - P).magnitude;
            double rMin = body.Radius + 0.5 * Math.Max(0.0, rv - body.Radius);
            Vector3d rel = cam - P;
            double r = rel.magnitude;
            if (r < rMin && r > 1.0) return P + rel * (rMin / r);
            return cam;
        }

        /// <summary>Puts the camera at a world position. skyWeight 0..1 = how far into the shot (skybox dimming).</summary>
        public void Apply(Vector3d cam, Quaternion rot, double tanV, double skyWeight)
        {
            Transform ct = Fc.transform;
            ct.position = (Vector3)cam;
            ct.rotation = rot;
            float fov = (float)(2.0 * Math.Atan(tanV) * 180.0 / Math.PI);
            Fc.SetFoV(fov);
            ScaledCamera sc = ScaledCamera.Instance;
            if (sc != null)
            {
                sc.transform.position = (Vector3)ScaledSpace.LocalToScaledSpace(cam);
                sc.transform.localRotation = rot;
                // Telephoto shots would blow the low-resolution skybox up into blurry smudges.
                float skyFov = Mathf.Max(fov, Mathf.Min(S.skyboxMinFov, OrigFov));
                if (sc.galaxyCamera != null && skyFov > fov) sc.galaxyCamera.fieldOfView = skyFov;
            }
            // Exposure: with a sunlit planet in frame the stars should be dim. The stock glare
            // dimming only works when the sun itself is near the view.
            if (galaxy != null)
            {
                float k = Mathf.Lerp(1f, S.skyboxBrightness, Mathf.Clamp01((float)skyWeight));
                galaxy.maxGalaxyColor = new Color(galaxyMax.r * k, galaxyMax.g * k, galaxyMax.b * k, galaxyMax.a);
            }
        }

        /// <summary>Gives the camera back to FlightCamera, at its own resume pose (no jump).</summary>
        public void HandBack()
        {
            if (released) return;
            released = true;
            try
            {
                if (Fc != null)
                {
                    if (Fc.AbortExternalControl == (Callback)OnAbort) Fc.AbortExternalControl = prevAbort;
                    Transform piv = Fc.GetPivot();
                    if (piv != null) piv.rotation = PivotRotation();
                    Fc.transform.localPosition = Vector3.back * Fc.Distance;
                    Fc.transform.localRotation = Quaternion.identity;
                    Fc.SetFoV(OrigFov);
                }
            }
            catch (Exception e) { Log.Warn("camera hand-back: " + e.Message); }
            try { if (Fc != null) Fc.ActivateUpdate(); } catch { }
            try { if (taken) RestoreScaledTarget(); } catch { }
            RestoreSky();
        }

        public void RestoreSky()
        {
            try { if (galaxy != null) galaxy.maxGalaxyColor = galaxyMax; } catch { }
        }

        // ------------------------------------------------------------------ projection

        /// <summary>Screen position (pixels, origin bottom-left) and radius of a sphere; false when behind the camera.</summary>
        public static bool ProjectSphere(Vector3d cam, Quaternion rot, double tanV, Vector3d centre, double radius,
                                         out Vector2 screen, out float radiusPx)
        {
            screen = Vector2.zero; radiusPx = 0f;
            float W = Screen.width, H = Screen.height;
            Vector3d rel = centre - cam;
            double dist = rel.magnitude;
            if (dist <= radius) return false;
            Vector3 q = Quaternion.Inverse(rot) * (Vector3)(rel / dist);
            if (q.z <= 0.01f) return false;
            double ndcX = q.x / (q.z * tanV * Aspect), ndcY = q.y / (q.z * tanV);
            double tanAlpha = Math.Tan(Math.Asin(radius / dist));
            screen = new Vector2((float)((ndcX + 1) * 0.5 * W), (float)((ndcY + 1) * 0.5 * H));
            radiusPx = (float)(tanAlpha / (q.z * tanV) * 0.5 * H);
            return Math.Abs(ndcX) < 1.5 && Math.Abs(ndcY) < 1.5;
        }

        /// <summary>Where to draw the "vessel is here" ring: visible, small, not hidden by the body, inside the letterbox.</summary>
        public static bool ProjectMarker(Vector3d cam, Quaternion rot, double tanV, Vector3d vesselPos, double vesselSize,
                                         CelestialBody body, double barFraction, out Vector2 screen)
        {
            return ProjectMarker(cam, rot, tanV, vesselPos, vesselSize, body, barFraction, out screen, out _);
        }

        /// <summary>As above; <paramref name="fade"/> (1..0) takes the ring away as the craft itself
        /// becomes visible (about 1 to 2.5 px tall), well before it could outgrow the ring.</summary>
        public static bool ProjectMarker(Vector3d cam, Quaternion rot, double tanV, Vector3d vesselPos, double vesselSize,
                                         CelestialBody body, double barFraction, out Vector2 screen, out float fade)
        {
            screen = Vector2.zero;
            fade = 0f;
            float W = Screen.width, H = Screen.height;
            Vector3d vrel = vesselPos - cam;
            double vd = vrel.magnitude;
            if (vd < 1e-3) return false;
            Vector3 vq = Quaternion.Inverse(rot) * (Vector3)(vrel / vd);
            if (vq.z <= 0.01f) return false;
            double px = vesselSize / Math.Max(1.0, vd * vq.z) / (2.0 * tanV) * H;
            if (px >= 10.0 || Occluded(cam, vrel, vd, body.position, body.Radius)) return false;
            double x = Math.Max(0.0, Math.Min(1.0, (px - 1.0) / 1.5));
            fade = (float)(1.0 - x * x * (3.0 - 2.0 * x));
            double ndcX = vq.x / (vq.z * tanV * Aspect), ndcY = vq.y / (vq.z * tanV);
            double inner = 1.0 - 2.0 * barFraction - 0.04;
            screen = new Vector2((float)((ndcX + 1) * 0.5 * W), (float)((ndcY + 1) * 0.5 * H));
            return Math.Abs(ndcX) < 0.95 && Math.Abs(ndcY) < inner;
        }

        private static bool Occluded(Vector3d cam, Vector3d toVessel, double vd, Vector3d P, double R)
        {
            Vector3d d = toVessel / vd;
            Vector3d oc = cam - P;
            double b = Vector3d.Dot(oc, d);
            double c = oc.sqrMagnitude - R * R;
            double disc = b * b - c;
            if (disc < 0) return false;
            double hit = -b - Math.Sqrt(disc);
            return hit > 0 && hit < vd;
        }
    }
}
