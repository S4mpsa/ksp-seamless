using System;
using UnityEngine;

namespace Seamless
{
    /// <summary>
    /// Walks a camera path (pose at s = 0..1) by perceived distance, as the departure flights do:
    /// the log of the distance to what is near, plus the view's turning, plus the zoom's change,
    /// at a speed and acceleration capped by flightMaxRate / flightMaxAccel (a cosine ramp up, a
    /// cruise, the same ramp down), the whole fitted between a shortest and a longest duration.
    /// Used for the zooms that follow a load, so a long zoom no longer races (7.8 e-folds in 2.5 s)
    /// while a short one crawls, and both halves of a transition move at the same pace.
    /// </summary>
    internal sealed class Pace
    {
        private double[] lam;
        private double len, peak, ramp, profT;
        /// <summary>The duration (transition seconds).</summary>
        public double T { get; private set; }
        public double EFolds { get { return len; } }

        public delegate void PoseAt(double s, out Vector3d cam, out Quaternion rot, out double tanV);

        public static Pace Build(PoseAt pose, Func<Vector3d, double> near, double maxRate, double maxAccel, double tMin, double tMax)
        {
            try
            {
                const int n = 201;
                var lam = new double[n];
                Vector3 prevFwd = Vector3.forward;
                double prevNear = 1.0, prevTan = 1.0;
                for (int i = 0; i < n; i++)
                {
                    double s = (double)i / (n - 1);
                    pose(s, out Vector3d cam, out Quaternion rot, out double tan);
                    double nr = Math.Max(1.0, near(cam));
                    Vector3 fwd = rot * Vector3.forward;
                    tan = Math.Max(1e-4, tan);
                    if (i > 0)
                        lam[i] = lam[i - 1] + Math.Abs(Math.Log(nr / prevNear)) + Vector3.Angle(prevFwd, fwd) * Math.PI / 180.0
                                 + Math.Abs(Math.Log(tan / prevTan));
                    prevFwd = fwd; prevNear = nr; prevTan = tan;
                }
                double L = lam[n - 1];
                if (L < 1e-6) return null;
                double V = Math.Max(0.1, maxRate), A = Math.Max(0.05, maxAccel);
                double Ta = V * Math.PI / (2.0 * A);
                if (L < V * Ta) { V = Math.Sqrt(2.0 * A * L / Math.PI); Ta = V * Math.PI / (2.0 * A); }
                double Tc = Math.Max(0.0, (L - V * Ta) / V);
                double Tp = 2.0 * Ta + Tc;
                var p = new Pace { lam = lam, len = L, peak = V, ramp = Ta, profT = Tp };
                p.T = Math.Max(tMin, Math.Min(tMax, Tp));
                return p;
            }
            catch (Exception e) { Log.Warn("zoom pacing: " + e.Message); return null; }
        }

        /// <summary>Path position (0..1) at <paramref name="time"/> transition seconds into it.</summary>
        public double U(double time)
        {
            double tau = Math.Min(profT, Math.Max(0.0, time * profT / Math.Max(1e-6, T)));
            double l = Profile(tau);
            int n = lam.Length;
            if (l <= 0.0) return 0.0;
            if (l >= len) return 1.0;
            int lo = 0, hi = n - 1;
            while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (lam[mid] < l) lo = mid; else hi = mid; }
            double a = lam[lo], b = lam[hi];
            double f = b > a ? (l - a) / (b - a) : 0.0;
            return (lo + f) / (n - 1);
        }

        private double Profile(double tau)
        {
            double Ta = ramp, Vp = peak, Tt = profT;
            if (tau <= 0.0) return 0.0;
            if (tau >= Tt) return len;
            if (tau < Ta) return Vp * (0.5 * tau - Ta / (2.0 * Math.PI) * Math.Sin(Math.PI * tau / Ta));
            double rampLen = 0.5 * Vp * Ta;
            if (tau <= Tt - Ta) return rampLen + Vp * (tau - Ta);
            double r = Tt - tau;
            return len - Vp * (0.5 * r - Ta / (2.0 * Math.PI) * Math.Sin(Math.PI * r / Ta));
        }
    }
}
