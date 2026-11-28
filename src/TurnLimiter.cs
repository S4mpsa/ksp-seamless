using System;
using UnityEngine;

namespace Seamless
{
    /// <summary>
    /// The last stage of every transition camera pose: the camera's rotation follows the planned
    /// one exactly while the plan turns within the caps (maxTurnRate, maxTurnAccel); only where it
    /// goes past them does the limiter take over, with its turning speed and acceleration capped, so a
    /// kink, a flip or a jump anywhere in a planned path - an opening turn that starts at full
    /// speed, a subject swinging past close to the camera, one frame off - shows as a quick but
    /// smooth turn, never a jerk. The planned path's own turning is fed forward, so a smooth path
    /// is followed without lag; only what goes past the caps is smoothed. KSP's world frame turning
    /// (the rotating frame near a planet, switched on or off at a craft switch) is frame motion,
    /// not camera motion: the state turns with it.
    /// </summary>
    internal sealed class TurnLimiter
    {
        private bool has;
        private bool engaged;           // smoothing (the plan went past the caps); else the plan passes through
        private Quaternion cur, prevTarget;
        private Vector3 omega;          // world-frame angular velocity, rad/s
        private int lastFrame = -1;
        private QuaternionD lastFrameRot;
        private bool frameRotSet;

        /// <summary>rad/s and rad/s^2; a rate of 0 turns the limiter off.</summary>
        public double MaxRate, MaxAccel;

        public void Reset()
        {
            has = false;
            engaged = false;
            omega = Vector3.zero;
            lastFrame = -1;
            frameRotSet = false;
        }

        /// <summary>For the frame probe: smoothing now, and how far (deg) the camera is from the plan.</summary>
        public bool Engaged { get { return has && engaged; } }
        public float Gap { get { return has ? Quaternion.Angle(cur, prevTarget) : 0f; } }

        /// <summary>True when the camera has caught up with the planned rotation.</summary>
        public bool Settled { get { return !has || MaxRate <= 0 || !engaged || Quaternion.Angle(cur, prevTarget) < 0.1f; } }

        /// <summary>The rotation to show this frame for the planned <paramref name="target"/>;
        /// <paramref name="speed"/> = the transition clock's speed (transitionTimeScale).</summary>
        public Quaternion Step(Quaternion target, double speed)
        {
            if (MaxRate <= 0.0) { has = false; return target; }
            int frame = Time.frameCount;
            if (!has)
            {
                has = true;
                cur = prevTarget = target;
                omega = Vector3.zero;
                engaged = false;
                lastFrame = frame;
                frameRotSet = TryFrameRot(out lastFrameRot);
                return cur;
            }
            if (frame == lastFrame) { prevTarget = target; return cur; }   // a second call in the same frame
            lastFrame = frame;
            double dt = Math.Min(Time.unscaledDeltaTime, 1.0 / 30.0) * speed;
            if (dt <= 1e-6) { prevTarget = target; return cur; }

            // KSP's world frame turned (rotating frame): turn the state with it
            if (TryFrameRot(out QuaternionD fr))
            {
                if (frameRotSet)
                {
                    Quaternion c = (Quaternion)(fr * QuaternionD.Inverse(lastFrameRot));
                    if (Quaternion.Angle(Quaternion.identity, c) > 1e-5f)
                    {
                        cur = Norm(c * cur);
                        prevTarget = Norm(c * prevTarget);
                        omega = c * omega;
                    }
                }
                lastFrameRot = fr;
                frameRotSet = true;
            }

            Vector3 wPlan = AngVel(prevTarget, target, dt);
            prevTarget = target;
            if (!engaged)
            {
                // the plan within the caps (a little slack for uneven frames): followed exactly
                bool rateOk = wPlan.magnitude <= MaxRate * 1.05;
                bool accelOk = (wPlan - omega).magnitude <= MaxAccel * dt * 2.0 + 0.05;
                if (rateOk && accelOk) { cur = target; omega = wPlan; return cur; }
                engaged = true;
            }
            Vector3 wT = Clamp(wPlan, MaxRate);
            // the gap to the plan, closed at the speed that can still stop on it: the braking curve
            // is on the speed relative to the plan, and braking may use twice the acceleration cap -
            // the old curve on the absolute speed overshot when the plan stopped and turned back
            Vector3 e = AngVel(cur, target, 1.0);
            double th = e.magnitude;
            Vector3 vrStar = Vector3.zero;
            if (th > 1e-7)
            {
                double c = Math.Min(MaxRate, Math.Sqrt(2.0 * MaxAccel * th));
                c = Math.Min(c, th / dt);   // never past the plan in one frame
                vrStar = e * (float)(c / th);
            }
            Vector3 want = Clamp(wT + vrStar, MaxRate);
            Vector3 dw = want - omega;
            Vector3 vr = omega - wT;
            double cap = MaxAccel * dt * (Vector3.Dot(dw, vr) < 0f ? 2.0 : 1.0);
            omega += Clamp(dw, cap);
            double ang = omega.magnitude * dt;
            if (ang > 1e-9)
                cur = Norm(Quaternion.AngleAxis((float)(ang * 180.0 / Math.PI), omega.normalized) * cur);
            // caught up: back to following the plan exactly
            if (Quaternion.Angle(cur, target) < 0.02f && (omega - wPlan).magnitude < 0.05f)
            {
                cur = target; omega = wPlan; engaged = false;
            }
            return cur;
        }

        /// <summary>World-frame angular velocity (rad/s) turning a into b over dt.</summary>
        private static Vector3 AngVel(Quaternion a, Quaternion b, double dt)
        {
            Quaternion d = b * Quaternion.Inverse(a);
            if (d.w < 0f) d = new Quaternion(-d.x, -d.y, -d.z, -d.w);   // the short way round
            d.ToAngleAxis(out float deg, out Vector3 axis);
            if (float.IsNaN(deg) || deg < 1e-6f || axis.sqrMagnitude < 1e-12f) return Vector3.zero;
            if (deg > 180f) deg -= 360f;
            return axis.normalized * (float)(deg * Math.PI / 180.0 / dt);
        }

        private static Vector3 Clamp(Vector3 v, double max)
        {
            double m = v.magnitude;
            return m > max && m > 0 ? v * (float)(max / m) : v;
        }

        private static Quaternion Norm(Quaternion q)
        {
            float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return n > 1e-6f ? new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n) : Quaternion.identity;
        }

        private static bool TryFrameRot(out QuaternionD q)
        {
            q = QuaternionD.identity;
            try { q = Planetarium.Rotation; return true; }
            catch { return false; }
        }
    }
}
