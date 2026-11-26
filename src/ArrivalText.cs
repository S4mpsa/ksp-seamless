using System;
using System.Collections.Generic;
using System.Globalization;

namespace Seamless
{
    internal static class Arrival
    {
        /// <summary>Closed orbit that stays inside the body's sphere of influence.</summary>
        public static bool IsCaptured(Orbit o, CelestialBody b)
        {
            if (o == null || b == null) return false;
            if (o.eccentricity >= 1.0) return false;
            if (o.ApR >= b.sphereOfInfluence) return false;
            return o.patchEndTransition != Orbit.PatchTransitionType.ESCAPE;
        }

        /// <summary>The star whose light falls on <paramref name="b"/> (null when b is a star or has none).</summary>
        public static CelestialBody StarOf(CelestialBody b)
        {
            CelestialBody s = b;
            for (int i = 0; i < 16 && s != null; i++)
            {
                if (s.isStar) return s == b ? null : s;
                if (s.referenceBody == null || s.referenceBody == s) break;
                s = s.referenceBody;
            }
            return null;
        }
    }

    internal static class ArrivalText
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string BodyName(CelestialBody b)
        {
            string n = b.displayName;
            try { n = n.LocalizeRemoveGender(); } catch { }
            if (string.IsNullOrEmpty(n)) n = b.bodyName;
            return n;
        }

        public static string Title(CelestialBody b) { return BodyName(b); }

        public static string Details(Vessel v, CelestialBody b, Settings s)
        {
            var lines = new List<string> { OrbitClass(v.orbit, b) };
            if (s.showApPe)
                lines.Add("Ap " + Dist(v.orbit.ApA) + "   ·   Pe " + Dist(v.orbit.PeA));
            if (s.showMissionTime)
            {
                string mt = MissionTime(v.missionTime);
                if (mt != null) lines.Add(mt);
            }
            if (s.showPlanetFacts) lines.AddRange(CardFacts.Planet(b));
            return string.Join("\n", lines.ToArray());
        }

        public static string OrbitClass(Orbit o, CelestialBody b)
        {
            // the body name is the title right above, so no "Low Duna Orbit"
            if (o == null) return "Orbit";
            if (b.rotates && b.rotationPeriod > 0 && o.eccentricity < 0.05
                && Math.Abs(o.period - b.rotationPeriod) / b.rotationPeriod < 0.02)
                return "Synchronous Orbit";
            double thr = b.scienceValues != null ? b.scienceValues.spaceAltitudeThreshold : 250000.0;
            if (o.ApA < thr) return "Low Orbit";
            if (o.PeA > thr) return "High Orbit";
            return "Elliptical Orbit";
        }

        public static string Dist(double m)
        {
            double km = m / 1000.0;
            if (Math.Abs(km) < 1000000.0) return km.ToString("N0", Inv) + " km";
            return (km / 1000000.0).ToString("N2", Inv) + " Gm";
        }

        public static string MissionTime(double seconds)
        {
            if (seconds <= 0 || double.IsNaN(seconds)) return null;
            double daySec = 21600.0;
            try { daySec = KSPUtil.dateTimeFormatter.Day; } catch { }
            long days = (long)Math.Floor(seconds / daySec);
            if (days >= 1) return days.ToString("N0", Inv) + (days == 1 ? " day" : " days") + " into the mission";
            long hours = (long)Math.Floor(seconds / 3600.0);
            if (hours >= 1) return hours + (hours == 1 ? " hour" : " hours") + " into the mission";
            long minutes = Math.Max(1, (long)Math.Floor(seconds / 60.0));
            return minutes + (minutes == 1 ? " minute" : " minutes") + " into the mission";
        }
    }
}
