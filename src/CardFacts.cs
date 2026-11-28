using System;
using System.Collections.Generic;
using System.Globalization;

namespace Seamless
{
    /// <summary>Extra lines for the cards: the save being loaded (funds, science, reputation,
    /// flights), a craft's next event, a planet's facts.</summary>
    internal static class CardFacts
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------------ save

        /// <summary>Lines for a save being loaded, read from the save itself (its scenarios are not
        /// running yet at the main menu).</summary>
        public static List<string> Save(Game g)
        {
            var lines = new List<string>();
            try
            {
                double funds = double.NaN, sci = double.NaN, rep = double.NaN;
                if (g.scenarios != null)
                    foreach (ProtoScenarioModule m in g.scenarios)
                    {
                        if (m == null) continue;
                        if (m.moduleName == "Funding") funds = Value(m, "funds");
                        else if (m.moduleName == "ResearchAndDevelopment") sci = Value(m, "sci");
                        else if (m.moduleName == "Reputation") rep = Value(m, "rep");
                    }
                var parts = new List<string>();
                if (g.Mode == Game.Modes.CAREER && !double.IsNaN(funds)) parts.Add(funds.ToString("N0", Inv) + " funds");
                if (g.Mode != Game.Modes.SANDBOX && !double.IsNaN(sci)) parts.Add(sci.ToString("N0", Inv) + " science");
                if (g.Mode == Game.Modes.CAREER && !double.IsNaN(rep)) parts.Add(rep.ToString("N0", Inv) + " reputation");
                if (parts.Count > 0) lines.Add(string.Join("   ·   ", parts.ToArray()));
            }
            catch { }
            try
            {
                int flights = 0;
                if (g.flightState != null && g.flightState.protoVessels != null)
                    foreach (ProtoVessel pv in g.flightState.protoVessels)
                        if (pv != null && IsFlight(pv.vesselType.ToString())) flights++;
                if (flights > 0) lines.Add(flights.ToString("N0", Inv) + (flights == 1 ? " flight in progress" : " flights in progress"));
            }
            catch { }
            return lines;
        }

        private static double Value(ProtoScenarioModule m, string key)
        {
            try
            {
                ConfigNode d = m.GetData();
                string s = d != null ? d.GetValue(key) : null;
                if (s != null && double.TryParse(s, NumberStyles.Float, Inv, out double v)) return v;
            }
            catch { }
            return double.NaN;
        }

        private static bool IsFlight(string type)
        {
            switch (type)
            {
                case "Debris": case "SpaceObject": case "Unknown": case "Flag":
                case "DeployedScienceController": case "DeployedSciencePart": case "DeployedGroundPart": case "DroppedPart":
                    return false;
                default:
                    return true;
            }
        }

        // ------------------------------------------------------------------ craft

        /// <summary>The craft's next event, if any: the next maneuver node, an SOI change along its
        /// patches (arrival at a body, or leaving one; only while in space), or an alarm set for it
        /// (named as an alarm: its title is the player's own) - the soonest.</summary>
        public static string NextEvent(Vessel v)
        {
            if (v == null) return null;
            double now = Planetarium.GetUniversalTime();
            double best = double.MaxValue;
            string what = null;
            void Offer(double ut, string label)
            {
                if (ut > now && ut < best && !string.IsNullOrEmpty(label)) { best = ut; what = label; }
            }
            // maneuver nodes: live on a loaded craft, else its saved flight plan
            try
            {
                if (v.loaded && v.patchedConicSolver != null && v.patchedConicSolver.maneuverNodes != null)
                {
                    foreach (ManeuverNode n in v.patchedConicSolver.maneuverNodes)
                        if (n != null) Offer(n.UT, "the next maneuver");
                }
                else
                {
                    ConfigNode fp = v.flightPlanNode;
                    if ((fp == null || !fp.HasNode("MANEUVER")) && v.protoVessel != null) fp = v.protoVessel.flightPlan;
                    if (fp != null)
                        foreach (ConfigNode n in fp.GetNodes("MANEUVER"))
                            if (double.TryParse(n.GetValue("UT"), NumberStyles.Float, Inv, out double ut)) Offer(ut, "the next maneuver");
                }
            }
            catch { }
            // SOI changes: only for a craft out in space (not landed, splashed, on the pad or in the
            // atmosphere), and the patch chain only where KSP keeps it current - a loaded craft with
            // its own solver whose first patch covers now (an unloaded craft's patches are stale)
            bool inSpace = v.situation == Vessel.Situations.ORBITING || v.situation == Vessel.Situations.SUB_ORBITAL
                           || v.situation == Vessel.Situations.ESCAPING;
            if (inSpace)
                try
                {
                    Orbit o = v.orbit;
                    bool live = v.loaded && v.patchedConicSolver != null && o != null
                                && o.StartUT <= now + 1.0 && o.EndUT > now;
                    bool found = false;
                    if (live)
                        for (int i = 0; i < 6 && o != null && !found; i++)
                        {
                            if (double.IsNaN(o.EndUT) || double.IsInfinity(o.EndUT) || o.nextPatch == null
                                || o.nextPatch.referenceBody == null || o.referenceBody == null) break;
                            if (o.patchEndTransition == Orbit.PatchTransitionType.ENCOUNTER)
                            {
                                Offer(o.EndUT, ArrivalText.BodyName(o.nextPatch.referenceBody) + " arrival");
                                found = true;
                            }
                            else if (o.patchEndTransition == Orbit.PatchTransitionType.ESCAPE)
                            {
                                // past an escape, an encounter further on is the bigger event
                                if (i == 0) Offer(o.EndUT, "leaving " + ArrivalText.BodyName(o.referenceBody));
                            }
                            else break;
                            o = o.nextPatch;
                        }
                    // without live patches an escape is still worked out from the orbit itself
                    o = v.orbit;
                    if (!live && o != null && o.referenceBody != null
                        && (o.eccentricity >= 1.0 || o.ApR > o.referenceBody.sphereOfInfluence)
                        && o.referenceBody.sphereOfInfluence < 1e15)
                    {
                        double ta = o.TrueAnomalyAtRadius(o.referenceBody.sphereOfInfluence);
                        double ut = o.GetUTforTrueAnomaly(ta, o.period > 0 && !double.IsInfinity(o.period) ? o.period : 0.0);
                        if (!double.IsNaN(ut) && !double.IsInfinity(ut)) Offer(ut, "leaving " + ArrivalText.BodyName(o.referenceBody));
                    }
                }
                catch { }
            // alarms set for this craft
            try
            {
                AlarmClockScenario ac = AlarmClockScenario.Instance;
                if (ac != null && ac.alarms != null)
                    foreach (AlarmTypeBase a in ac.alarms.Values)
                        if (a != null && a.vesselId != 0 && a.vesselId == v.persistentId)
                            Offer(a.ut, string.IsNullOrEmpty(a.title) ? "an alarm" : "the \"" + a.title + "\" alarm");
            }
            catch { }
            if (what == null) return null;
            return Duration(best - now) + " to " + what;
        }

        /// <summary>"2 days 3 hours", "4 hours 12 minutes", "12 minutes", "45 seconds" (KSP's day).</summary>
        public static string Duration(double s)
        {
            double daySec = 21600.0;
            try { daySec = KSPUtil.dateTimeFormatter.Day; } catch { }
            if (s < 60.0) { int sec = Math.Max(1, (int)Math.Round(s)); return sec + (sec == 1 ? " second" : " seconds"); }
            long d = (long)Math.Floor(s / daySec);
            s -= d * daySec;
            long h = (long)Math.Floor(s / 3600.0);
            long m = (long)Math.Floor((s - h * 3600.0) / 60.0);
            if (d > 0) return Unit(d, "day") + (h > 0 && d < 100 ? " " + Unit(h, "hour") : "");
            if (h > 0) return Unit(h, "hour") + (m > 0 ? " " + Unit(m, "minute") : "");
            return Unit(m, "minute");
        }

        private static string Unit(long n, string u) { return n.ToString("N0", Inv) + " " + u + (n == 1 ? "" : "s"); }

        // ------------------------------------------------------------------ planet

        /// <summary>A few facts about a planet for the arrival card.</summary>
        public static List<string> Planet(CelestialBody b)
        {
            var lines = new List<string>();
            if (b == null) return lines;
            try
            {
                lines.Add("Radius " + ArrivalText.Dist(b.Radius) + "   ·   Gravity " + b.GeeASL.ToString("0.00", Inv) + " g");
                if (b.atmosphere)
                {
                    double atm = b.atmospherePressureSeaLevel / 101.325;
                    string p = atm >= 10 ? atm.ToString("N0", Inv) : atm >= 0.1 ? atm.ToString("0.0#", Inv) : atm.ToString("0.###", Inv);
                    lines.Add("Atmosphere " + p + " atm, " + ArrivalText.Dist(b.atmosphereDepth) + " high"
                              + (b.atmosphereContainsOxygen ? ", oxygen" : ""));
                }
                else if (!b.isStar) lines.Add("No atmosphere");
                var last = new List<string>();
                if (!b.isStar)
                {
                    if (b.tidallyLocked) last.Add("Tidally locked");
                    else if (b.rotates && b.solarDayLength > 0 && !double.IsInfinity(b.solarDayLength)) last.Add("Day " + Duration(b.solarDayLength));
                }
                int n = b.orbitingBodies != null ? b.orbitingBodies.Count : 0;
                if (n > 0) last.Add(n + (b.isStar ? (n == 1 ? " planet" : " planets") : (n == 1 ? " moon" : " moons")));
                if (last.Count > 0) lines.Add(string.Join("   ·   ", last.ToArray()));
            }
            catch { }
            return lines;
        }
    }
}
