using System;
using System.Collections.Generic;
using UnityEngine;

namespace Seamless
{
    /// <summary>
    /// Hides every orbit line, map icon and node (and the CommNet link lines) and puts them back.
    /// DrawMode.OFF alone is not enough in KSP 1.12: it only stops redrawing, the Vectrosity line
    /// keeps its last shape on screen (a 2D line stays at its old screen position while the camera
    /// moves). So the line itself is switched off too. In Retooled 1.13 lines are batched and
    /// drawn per frame (OrbitLine is always null there); OFF is enough.
    /// The projected orbits (patched conics, maneuver plans) are drawn by a separate component,
    /// PatchedConicRenderer, which in Retooled keeps resubmitting its lines every frame while the
    /// game is paused in map view (the Tracking Station is map view, and time is frozen during a
    /// transition): it is switched off and its lines destroyed (they are rebuilt when it is back).
    /// The game can turn a renderer's mode back on (selection in the Tracking Station): Keep()
    /// re-applies the hiding every frame.
    /// </summary>
    internal sealed class OrbitLines
    {
        private struct Entry
        {
            public OrbitRendererBase R;
            public OrbitRendererBase.DrawMode Mode;
            public OrbitRendererBase.DrawIcons Icons;
            public bool Nodes, HadLine, LineActive;
        }

        private readonly List<Entry> entries = new List<Entry>();
        private readonly List<PatchedConicRenderer> patches = new List<PatchedConicRenderer>();
        // Renderers that have not run Start() yet (a craft made active this frame, the in-place
        // switch): switched off now they would never build their patch list, and the craft's
        // OrbitTargeter would throw on it every frame. They are switched off once started.
        private readonly List<PatchedConicRenderer> pending = new List<PatchedConicRenderer>();
        private CommNet.CommNetUI commNet;
        private bool commNetEnabled, commNetLine, commNetHadLine, commNetShown;
        private static readonly System.Reflection.FieldInfo CommNetShownField =
            typeof(CommNet.CommNetUI).GetField("isShown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        public static OrbitLines Hide()
        {
            var o = new OrbitLines();
            try
            {
                foreach (OrbitRendererBase r in (OrbitRendererBase[])UnityEngine.Object.FindObjectsOfTypeAll(typeof(OrbitRendererBase)))
                {
                    if (r == null) continue;
                    var e = new Entry { R = r, Mode = r.drawMode, Icons = r.drawIcons, Nodes = r.drawNodes };
#pragma warning disable 618
                    Vectrosity.VectorLine line = r.OrbitLine;
#pragma warning restore 618
                    if (line != null) { e.HadLine = true; e.LineActive = line.active; line.active = false; }
                    r.drawMode = OrbitRendererBase.DrawMode.OFF;
                    r.drawIcons = OrbitRendererBase.DrawIcons.NONE;
                    r.drawNodes = false;
                    o.entries.Add(e);
                }
            }
            catch (Exception e) { Log.Warn("hiding orbit lines: " + e.Message); }
            try
            {
                foreach (PatchedConicRenderer pr in UnityEngine.Object.FindObjectsOfType<PatchedConicRenderer>())
                {
                    if (pr == null || !pr.enabled) continue;
                    if (pr.patchRenders == null) { o.pending.Add(pr); continue; }
                    pr.enabled = false;
                    ClearPatchLines(pr);
                    o.patches.Add(pr);
                }
            }
            catch (Exception e) { Log.Warn("hiding projected orbits: " + e.Message); }
            try
            {
                CommNet.CommNetUI ui = CommNet.CommNetUI.Instance;
                if (ui != null)
                {
                    o.commNet = ui;
                    o.commNetEnabled = ui.enabled;
                    ui.enabled = false;
                    Vectrosity.VectorLine l = ui.Line;
                    if (l != null) { o.commNetHadLine = true; o.commNetLine = l.active; l.active = false; }
                    // the links are redrawn from a TimingManager late update, not the component's
                    // own update, so disabling it did not stop them: Hide() unhooks that
                    o.commNetShown = CommNetShownField != null && (bool)CommNetShownField.GetValue(ui);
                    if (o.commNetShown) ui.Hide();
                }
            }
            catch { }
            return o;
        }

        private static void ClearPatchLines(PatchedConicRenderer pr)
        {
            try
            {
                if (pr.patchRenders != null) foreach (PatchRendering r in pr.patchRenders) { try { r.DestroyVector(); } catch { } }
                if (pr.flightPlanRenders != null) foreach (PatchRendering r in pr.flightPlanRenders) { try { r.DestroyVector(); } catch { } }
            }
            catch { }
        }

        /// <summary>Per frame while hidden: modes the game switched back on are switched off again.</summary>
        public void Keep()
        {
            foreach (Entry e in entries)
            {
                OrbitRendererBase r = e.R;
                if (r == null) continue;
                if (r.drawMode != OrbitRendererBase.DrawMode.OFF) r.drawMode = OrbitRendererBase.DrawMode.OFF;
                if (r.drawIcons != OrbitRendererBase.DrawIcons.NONE) r.drawIcons = OrbitRendererBase.DrawIcons.NONE;
                r.drawNodes = false;
            }
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                PatchedConicRenderer pr = pending[i];
                if (pr == null) { pending.RemoveAt(i); continue; }
                if (pr.patchRenders == null) continue;
                pending.RemoveAt(i);
                patches.Add(pr);
            }
            foreach (PatchedConicRenderer pr in patches)
                if (pr != null && pr.enabled) { pr.enabled = false; ClearPatchLines(pr); }
            try
            {
                // the game shows the links again on a map focus change (Tracking Station selection)
                if (commNet != null && CommNetShownField != null && (bool)CommNetShownField.GetValue(commNet))
                { commNetShown = true; commNet.Hide(); }
            }
            catch { }
        }

        /// <summary>Back as they were. The lines themselves only where they are drawn now (Tracking
        /// Station, map view); elsewhere the game switches them on again when the map opens.</summary>
        public void Restore()
        {
            bool lines = HighLogic.LoadedScene == GameScenes.TRACKSTATION || MapView.MapIsEnabled;
            foreach (Entry e in entries)
            {
                OrbitRendererBase r = e.R;
                if (r == null) continue;   // destroyed with its scene
                try
                {
                    r.drawMode = e.Mode;
                    r.drawIcons = e.Icons;
                    r.drawNodes = e.Nodes;
#pragma warning disable 618
                    if (e.HadLine && lines && r.OrbitLine != null) r.OrbitLine.active = e.LineActive;
#pragma warning restore 618
                }
                catch { }
            }
            entries.Clear();
            foreach (PatchedConicRenderer pr in patches)
                try { if (pr != null) pr.enabled = true; } catch { }
            patches.Clear();
            pending.Clear();
            try
            {
                if (commNet != null)
                {
                    commNet.enabled = commNetEnabled;
                    if (commNetShown) commNet.Show();
                    if (commNetHadLine && lines && commNet.Line != null) commNet.Line.active = commNetLine;
                }
            }
            catch { }
            commNet = null;
        }
    }
}
