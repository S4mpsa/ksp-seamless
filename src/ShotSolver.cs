using System;
using UnityEngine;

namespace Seamless
{
    /// <summary>One framed shot, solved once when the cinematic starts.</summary>
    internal sealed class Shot
    {
        public string Mode;        // "far", "near" or "fallback"
        public Vector3d Offset;    // camera position minus the anchor (the camera pivot) at solve time
        public Quaternion Rot;     // camera rotation at solve time
        public double TanV;        // tan(vertical FOV / 2)
        public double Distance;    // camera to planet centre (log only)
        public double VesselDistance;
    }

    /// <summary>
    /// The framing: planet centre at (planetScreenX, middle of the letterbox), planet diameter =
    /// planetFraction of the height between the bars, north up.
    ///
    /// far:  the default FOV, the camera pulled back until the planet has that size. The camera
    ///       sits sunSideAngle beside the sun, the sun on the text side (sunFromTextSide; see
    ///       SunSideDir), or else on the vessel's side of the planet, turned at most maxSunAngle
    ///       away from the sun so the planet is lit. The vessel is then usually too small to see;
    ///       a marker shows where it is.
    /// near: the planet is far away (high orbits, just after an SOI change). The camera stays a
    ///       short way behind the vessel, which shows at (vesselScreenX, vesselScreenY) at about
    ///       vesselFraction of the screen, and the FOV narrows (telephoto) until the planet has
    ///       the right size.
    /// fallback: even the narrowest FOV cannot make the planet big enough from behind the
    ///       vessel; the camera moves towards the planet past the vessel.
    /// </summary>
    internal static class ShotSolver
    {
        public static Vector3d Ray(double ndcX, double ndcY, double tanV, double aspect)
        {
            return new Vector3d(ndcX * tanV * aspect, ndcY * tanV, 1.0).normalized;
        }

        private sealed class Ctx
        {
            public Settings S;
            public double Aspect, Sx, Kv, Vx, Vy;
            public Vector3d Anchor, P, SunDir, Up;
            public double R, Rv, Size;
            public Vector3d VHat;
            public bool SunSide;
            public bool WantVessel;   // far shot: keep the anchor (the vessel) in view   // far shot: lit from the text side instead of from the vessel's side

            /// <summary>tan of the planet's angular radius that gives the wanted size at this FOV.</summary>
            public double TanAlpha(double tanV)
            {
                // A disc centred off-axis at angle theta (horizontally) is stretched 1/cos(theta)
                // vertically on the screen plane.
                double cosTheta = Ray(Sx, 0, tanV, Aspect).z;
                return Kv * tanV * cosTheta;
            }

            public double TanVFor(double tanAlpha)
            {
                double tanV = tanAlpha / Kv;
                for (int i = 0; i < 8; i++)
                    tanV = tanAlpha / (Kv * Ray(Sx, 0, tanV, Aspect).z);
                return tanV;
            }
        }

        public static Shot Solve(Settings s, Vector3d anchor, CelestialBody body, Vector3d sunDir,
                                 double vesselSize, double aspect)
        {
            Ctx c = MakeCtx(s, anchor, body, sunDir, vesselSize, aspect);
            c.SunSide = s.sunFromTextSide;
            c.WantVessel = true;
            double tanV0 = Math.Tan(s.cameraFov * 0.5 * Math.PI / 180.0);
            double dFar = c.R / Math.Sin(Math.Atan(c.TanAlpha(tanV0)));
            double lFar = c.Size / (2.0 * tanV0 * s.vesselFraction);
            if (body.isStar)
            {
                // A star is not framed like a planet from far off (a telephoto shot of the sun is a
                // blinding disc): the camera stays near the craft at the normal field of view, the
                // star at its true size from there - framed only when the craft is that close anyway.
                // starShotZoomOut times the vessel-size distance: at that distance alone the craft
                // popped in at a tenth of the screen and the arrival had hardly any zoom left.
                if (dFar >= c.Rv + lFar) return Far(c, tanV0, dFar);
                Shot natural = Near(c, tanV0, true, s.starShotZoomOut);
                if (natural != null) return natural;
            }
            // High orbits: the camera pulls back (the planet smaller) until the vessel's distance from
            // the planet fits between the planet's spot and the vessel's spot on screen, at most
            // maxZoomOut times the usual distance.
            if (c.SunSide)
            {
                Vector3d rp = Ray(c.Sx, 0, tanV0, c.Aspect), rv = Ray(c.Vx, c.Vy, tanV0, c.Aspect);
                double sep = Math.Acos(Clamp(Vector3d.Dot(rp, rv)));
                double fit = c.Rv / Math.Tan(Math.Max(0.05, sep));
                dFar = Math.Max(dFar, Math.Min(dFar * s.maxZoomOut, fit));
            }
            if (dFar >= c.Rv + lFar)
                return Far(c, tanV0, dFar);
            Shot close = Near(c, tanV0) ?? Fallback(c);
            if (c.SunSide && !WellLit(c, close))
            {
                // the camera leaves the vessel for the sunlit side instead of showing a dark planet
                Shot far = Far(c, tanV0, dFar);
                far.Mode += " (instead of a badly lit " + close.Mode + " shot)";
                return far;
            }
            return close;
        }

        /// <summary>Always the far planet shot. <paramref name="sunSide"/> false: on the anchor's side
        /// of the planet (turned at most maxSunAngle to the lit side).</summary>
        public static Shot SolveFar(Settings s, Vector3d anchor, CelestialBody body, Vector3d sunDir, double aspect,
                                    bool sunSide = true)
        {
            Ctx c = MakeCtx(s, anchor, body, sunDir, 5.0, aspect);
            c.SunSide = sunSide && s.sunFromTextSide;
            double tanV0 = Math.Tan(s.cameraFov * 0.5 * Math.PI / 180.0);
            return Far(c, tanV0, c.R / Math.Sin(Math.Atan(c.TanAlpha(tanV0))));
        }

        private static Ctx MakeCtx(Settings s, Vector3d anchor, CelestialBody body, Vector3d sunDir,
                                   double vesselSize, double aspect)
        {
            var c = new Ctx
            {
                S = s,
                Aspect = aspect,
                Sx = 2.0 * s.planetScreenX - 1.0,
                Kv = s.planetFraction * (1.0 - 2.0 * s.barFraction),
                Vx = 2.0 * s.vesselScreenX - 1.0,
                Vy = 2.0 * s.vesselScreenY - 1.0,
                Anchor = anchor,
                P = body.position,
                R = body.Radius,
                SunDir = sunDir,
                Size = Math.Max(2.0, vesselSize),
            };
            Vector3d rel = anchor - c.P;
            c.Rv = rel.magnitude;
            c.VHat = rel / c.Rv;
            c.Up = UpHint(body, c.VHat);
            return c;
        }

        private static Vector3d UpHint(CelestialBody body, Vector3d vHat)
        {
            Vector3d up = body.transform != null ? (Vector3d)body.transform.up : Vector3d.up;
            if (up.sqrMagnitude < 1e-12) up = Vector3d.up;
            return up.normalized;
        }

        // ------------------------------------------------------------------ far

        private static Shot Far(Ctx c, double tanV, double d)
        {
            Vector3d dir = c.VHat;
            Vector3d lit;
            string mode = "far";
            if (c.SunSide && c.WantVessel && VesselInViewDir(c, tanV, d, out lit))
            {
                dir = lit;
                mode = "far, sun-side, vessel in view";
            }
            else if (c.SunSide && SunSideDir(c, tanV, out lit))
            {
                dir = lit;
                mode = c.WantVessel ? "far, sun-side (no lit angle shows the vessel)" : "far, sun-side";
            }
            else if (c.SunDir.sqrMagnitude > 0.5)
            {
                double ang = AngleDeg(dir, c.SunDir);
                if (ang > c.S.maxSunAngle)
                {
                    Vector3d axis = Vector3d.Cross(dir, c.SunDir);
                    if (axis.sqrMagnitude < 1e-10) axis = Vector3d.Cross(dir, c.Up);
                    if (axis.sqrMagnitude < 1e-10) axis = Perp(dir);
                    dir = Rotate(dir, axis.normalized, (ang - c.S.maxSunAngle) * Math.PI / 180.0).normalized;
                }
            }
            Vector3d cam = c.P + dir * d;
            Quaternion rot = Aim(-dir, Ray(c.Sx, 0, tanV, c.Aspect), c.Up);
            return new Shot { Mode = mode, Offset = cam - c.Anchor, Rot = rot, TanV = tanV, Distance = d, VesselDistance = (cam - c.Anchor).magnitude };
        }

        /// <summary>
        /// The camera sunSideAngle away from the sun as seen from the planet, on the side that
        /// puts the sun to the right of the view: the planet lit from the right, its dark edge on
        /// the left (53 degrees: about a fifth of the disc dark). Of the directions on that cone the
        /// one with the sun most to the right is taken (level with the sun when the sun is near the
        /// equator; still a lit planet, dark side lower left, when the sun is near a pole).
        /// </summary>
        private static bool SunSideDir(Ctx c, double tanV, out Vector3d dir)
        {
            dir = c.VHat;
            if (c.SunDir.sqrMagnitude < 0.5) return false;
            Vector3d sun = c.SunDir.normalized;
            Vector3d e1 = Vector3d.Cross(c.Up, sun);
            if (e1.magnitude < 1e-6) e1 = Perp(sun);
            e1 = e1.normalized;
            Vector3d e2 = Vector3d.Cross(sun, e1).normalized;
            double th = c.S.sunSideAngle * Math.PI / 180.0;
            Vector3d ray = Ray(c.Sx, 0, tanV, c.Aspect);
            double best = double.NegativeInfinity;
            for (int i = 0; i < 72; i++)
            {
                double psi = i * (2.0 * Math.PI / 72.0);
                Vector3d w = e1 * Math.Cos(psi) + e2 * Math.Sin(psi);
                Vector3d d = (sun * Math.Cos(th) + w * Math.Sin(th)).normalized;
                Quaternion rot = Aim(-d, ray, c.Up);
                double side = Vector3d.Dot(sun, (Vector3d)(rot * Vector3.right));
                if (side > best) { best = side; dir = d; }
            }
            return true;
        }

        /// <summary>
        /// A far-shot camera direction with the planet mostly lit and the vessel in view: of ~1500
        /// directions around the planet (at the shot's distance), those with the sun at most 80
        /// degrees from the camera (more than half the disc lit) and the vessel in front of the
        /// camera, not behind the planet and inside the letterbox; the best by lighting (close to
        /// sunSideAngle, sun to the right) and the vessel's spot (towards vesselScreenX / Y, the
        /// lower right, away from the planet on the left and the title). False when none shows the
        /// vessel.
        /// </summary>
        private static bool VesselInViewDir(Ctx c, double tanV, double d, out Vector3d dir)
        {
            dir = c.VHat;
            if (c.SunDir.sqrMagnitude < 0.5) return false;
            Vector3d sun = c.SunDir.normalized;
            Vector3d ray = Ray(c.Sx, 0, tanV, c.Aspect);
            double inner = 1.0 - 2.0 * c.S.barFraction - 0.08;
            double prefX = 2.0 * c.S.vesselScreenX - 1.0, prefY = 2.0 * c.S.vesselScreenY - 1.0;
            double best = double.PositiveInfinity;
            const int N = 1500;
            double golden = Math.PI * (3.0 - Math.Sqrt(5.0));
            for (int i = 0; i < N; i++)
            {
                double y = 1.0 - 2.0 * (i + 0.5) / N, rr = Math.Sqrt(Math.Max(0.0, 1.0 - y * y)), az = golden * i;
                var cand = new Vector3d(Math.Cos(az) * rr, y, Math.Sin(az) * rr);
                double alpha = AngleDeg(cand, sun);
                if (alpha > 80.0) continue;
                Vector3d cam = c.P + cand * d;
                Vector3d vrel = c.Anchor - cam;
                double vd = vrel.magnitude;
                if (vd < 1.0) continue;
                Quaternion rot = Aim(-cand, ray, c.Up);
                Vector3 q = Quaternion.Inverse(rot) * (Vector3)(vrel / vd);
                if (q.z <= 0.05f) continue;
                double nx = q.x / (q.z * tanV * c.Aspect), ny = q.y / (q.z * tanV);
                if (Math.Abs(nx) > 0.9 || Math.Abs(ny) > inner) continue;
                if (Paths.Blocked(cam, c.Anchor, c.P, c.R)) continue;
                double sr = Vector3d.Dot(sun, (Vector3d)(rot * Vector3.right));
                double la = (alpha - c.S.sunSideAngle) / 15.0;
                double cost = la * la + Math.Max(0.0, 0.5 - sr) * 4.0
                            + Math.Max(0.0, prefX - nx) * 1.5 + Math.Abs(ny - prefY) * 0.3;
                if (cost < best) { best = cost; dir = cand; }
            }
            return !double.IsPositiveInfinity(best);
        }

        /// <summary>Near / fallback shots keep the camera by the vessel, so the light is whatever it
        /// is there: they are used only when that already lights the planet about as the far shot
        /// would (sun to the right, sunSideAngle give or take ~20 degrees).</summary>
        private static bool WellLit(Ctx c, Shot shot)
        {
            if (shot == null) return false;
            if (c.SunDir.sqrMagnitude < 0.5) return true;
            Vector3d sun = c.SunDir.normalized;
            double phase = AngleDeg(c.Anchor + shot.Offset - c.P, sun);
            double right = Vector3d.Dot(sun, (Vector3d)(shot.Rot * Vector3.right));
            return phase >= c.S.sunSideAngle - 25.0 && phase <= c.S.sunSideAngle + 20.0 && right > 0.2;
        }

        // ------------------------------------------------------------------ near

        private static Shot Near(Ctx c, double tanV0, bool naturalSize = false, double zoomOut = 1.0)
        {
            double tanV = tanV0, d = 0, L = 0, gamma = 0;
            if (naturalSize) L = zoomOut * c.Size / (2.0 * tanV * c.S.vesselFraction);
            for (int i = 0; i < (naturalSize ? 0 : 12); i++)
            {
                Vector3d rp = Ray(c.Sx, 0, tanV, c.Aspect);
                Vector3d rv = Ray(c.Vx, c.Vy, tanV, c.Aspect);
                gamma = Math.Acos(Clamp(Vector3d.Dot(rp, rv)));
                L = c.Size / (2.0 * tanV * c.S.vesselFraction);
                double sg = Math.Sin(gamma);
                double disc = c.Rv * c.Rv - L * L * sg * sg;
                if (disc <= 0) return null;
                d = L * Math.Cos(gamma) + Math.Sqrt(disc);
                if (d <= c.R * 1.001) return null;
                double tanAlpha = Math.Tan(Math.Asin(c.R / d));
                tanV = c.TanVFor(tanAlpha);
            }
            double minTanV = Math.Tan(c.S.minFov * 0.5 * Math.PI / 180.0);
            if (tanV < minTanV || double.IsNaN(tanV)) return null;

            Vector3d e1 = Ray(c.Sx, 0, tanV, c.Aspect);
            Vector3d e2 = Ray(c.Vx, c.Vy, tanV, c.Aspect);
            // the final camera-space angle may differ a hair from the last iteration's
            gamma = Math.Acos(Clamp(Vector3d.Dot(e1, e2)));
            {
                double sg = Math.Sin(gamma);
                double disc = c.Rv * c.Rv - L * L * sg * sg;
                if (disc <= 0) return null;
                d = L * Math.Cos(gamma) + Math.Sqrt(disc);
            }

            // Angle at the planet between planet->vessel and planet->camera.
            double cosPsi = Clamp((d * d + c.Rv * c.Rv - L * L) / (2.0 * d * c.Rv));
            double psi = Math.Acos(cosPsi);

            Vector3d w0 = Vector3d.Cross(c.VHat, c.Up);
            if (w0.sqrMagnitude < 1e-8) w0 = Perp(c.VHat);
            w0 = w0.normalized;
            Vector3d w1 = Vector3d.Cross(c.VHat, w0).normalized;

            // The camera may sit anywhere on the cone around planet->vessel; pick the spot that
            // keeps north up.
            double best = double.NegativeInfinity, bestPhi = 0;
            for (int i = 0; i < 72; i++)
            {
                double phi = i * (Math.PI * 2.0 / 72.0);
                double sc = Score(c, psi, d, L, w0, w1, phi, e1, e2, out _, out _);
                if (sc > best) { best = sc; bestPhi = phi; }
            }
            double step = Math.PI * 2.0 / 72.0;
            for (int pass = 0; pass < 3; pass++)
            {
                double lo = bestPhi - step, hi = bestPhi + step;
                for (int i = 0; i <= 20; i++)
                {
                    double phi = lo + (hi - lo) * i / 20.0;
                    double sc = Score(c, psi, d, L, w0, w1, phi, e1, e2, out _, out _);
                    if (sc > best) { best = sc; bestPhi = phi; }
                }
                step /= 10.0;
            }
            Score(c, psi, d, L, w0, w1, bestPhi, e1, e2, out Vector3d cam, out Quaternion rot);
            return new Shot { Mode = naturalSize ? "near, natural size" : "near", Offset = cam - c.Anchor, Rot = rot, TanV = tanV, Distance = d, VesselDistance = L };
        }

        private static double Score(Ctx c, double psi, double d, double L, Vector3d w0, Vector3d w1, double phi,
                                    Vector3d e1, Vector3d e2, out Vector3d cam, out Quaternion rot)
        {
            Vector3d w = w0 * Math.Cos(phi) + w1 * Math.Sin(phi);
            Vector3d cHat = c.VHat * Math.Cos(psi) + w * Math.Sin(psi);
            cam = c.P + cHat * d;
            Vector3d a = -cHat;
            Vector3d b = c.VHat * c.Rv - cHat * d;   // vessel - camera, without large-number cancellation
            b = b / L;
            rot = PairRotation(a, b, e1, e2);
            return Vector3.Dot(rot * Vector3.up, (Vector3)c.Up);
        }

        // ------------------------------------------------------------------ fallback

        private static Shot Fallback(Ctx c)
        {
            double tanV = Math.Tan(c.S.minFov * 0.5 * Math.PI / 180.0);
            double d = c.R / Math.Sin(Math.Atan(c.TanAlpha(tanV)));
            Vector3d cam = c.P + c.VHat * d;
            Quaternion rot = Aim(-c.VHat, Ray(c.Sx, 0, tanV, c.Aspect), c.Up);
            return new Shot { Mode = "fallback", Offset = cam - c.Anchor, Rot = rot, TanV = tanV, Distance = d, VesselDistance = (cam - c.Anchor).magnitude };
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Rotation that puts world direction <paramref name="worldDir"/> on camera ray
        /// <paramref name="camRay"/> (which lies in the camera's horizontal plane), north up.</summary>
        public static Quaternion Aim(Vector3d worldDir, Vector3d camRay, Vector3d up)
        {
            Vector3 f = (Vector3)worldDir.normalized;
            Vector3 u = (Vector3)up;
            if (Mathf.Abs(Vector3.Dot(f, u)) > 0.999f) u = (Vector3)Perp(worldDir);
            Quaternion q0 = Quaternion.LookRotation(f, u);
            return q0 * Quaternion.FromToRotation((Vector3)camRay, Vector3.forward);
        }

        /// <summary>Rotation that puts world direction <paramref name="worldDir"/> on any camera ray, up roughly <paramref name="up"/>.</summary>
        public static Quaternion AimAnyRay(Vector3d worldDir, Vector3d camRay, Vector3d up)
        {
            Vector3 f = (Vector3)worldDir.normalized;
            Vector3 u = (Vector3)up.normalized;
            if (Mathf.Abs(Vector3.Dot(f, u)) > 0.999f) u = (Vector3)Perp(worldDir);
            return Quaternion.LookRotation(f, u) * Quaternion.FromToRotation((Vector3)camRay.normalized, Vector3.forward);
        }

        /// <summary>Rotation mapping camera rays e1, e2 onto world directions a, b (same angle).</summary>
        public static Quaternion PairRotation(Vector3d a, Vector3d b, Vector3d e1, Vector3d e2)
        {
            Vector3d f2 = (b - a * Vector3d.Dot(a, b)).normalized;
            Vector3d g2 = (e2 - e1 * Vector3d.Dot(e1, e2)).normalized;
            Quaternion qf = Quaternion.LookRotation((Vector3)a, (Vector3)f2);
            Quaternion qe = Quaternion.LookRotation((Vector3)e1, (Vector3)g2);
            return qf * Quaternion.Inverse(qe);
        }

        public static Vector3d Rotate(Vector3d v, Vector3d k, double theta)
        {
            double cs = Math.Cos(theta), sn = Math.Sin(theta);
            return v * cs + Vector3d.Cross(k, v) * sn + k * (Vector3d.Dot(k, v) * (1.0 - cs));
        }

        public static Vector3d Perp(Vector3d v)
        {
            Vector3d p = Vector3d.Cross(v, Vector3d.right);
            if (p.sqrMagnitude < 1e-8) p = Vector3d.Cross(v, Vector3d.forward);
            return p.normalized;
        }

        public static double AngleDeg(Vector3d a, Vector3d b)
        {
            return Math.Acos(Clamp(Vector3d.Dot(a.normalized, b.normalized))) * 180.0 / Math.PI;
        }

        private static double Clamp(double x) { return x < -1 ? -1 : (x > 1 ? 1 : x); }
    }
}
