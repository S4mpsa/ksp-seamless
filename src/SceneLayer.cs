using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Seamless
{
    /// <summary>
    /// The frozen frame as part of the 3D picture, for HDR output (KSP Retooled).
    ///
    /// Retooled maps the 3D picture to the display's range (black level, highlights) and leaves the
    /// UI at paper white. It tells the two apart by what the cameras had drawn before its own
    /// camera at depth 5: whatever is drawn later, or differs from that picture, is UI. A frozen
    /// frame on the overlay canvas is UI to it, so the picture lost its HDR range the moment it
    /// froze and got it back when the new scene showed.
    ///
    /// While HDR output is on, the frozen frame is therefore drawn by a camera of its own below
    /// that depth, and it is captured there as well: the 3D picture alone, without the bars and
    /// the card, which the overlay keeps drawing over it as UI. Without HDR output nothing here
    /// is used.
    ///
    /// The camera also has to come after Retooled's anti-aliasing, which draws the 3D cameras
    /// into a texture and puts the result on the screen from a camera at depth 4.5: before that
    /// the screen still holds the last frame as it went to the display.
    /// </summary>
    internal sealed class SceneLayer
    {
        // Retooled's depth for overlays on the finished 3D picture: after its anti-aliasing (4.5),
        // before its HDR camera (5)
        private const float Depth = 4.8f;
        // a camera that draws nothing still gets this event; some Unity versions skip AfterEverything
        private const CameraEvent CopyEvent = CameraEvent.AfterForwardAlpha;
        // a layer and a place of its own, so the camera draws the frozen frame and nothing else
        private const int Layer = 14;
        private static readonly Vector3 Place = new Vector3(9000f, -9000f, 9000f);

        private static readonly PropertyInfo HdrMain, HdrActive;

        private readonly GameObject root;
        private readonly Camera cam;
        private readonly Canvas canvas;
        private CommandBuffer copy;
        private RenderTexture shot;

        static SceneLayer()
        {
            try
            {
                // Unity 2021+; KSP 1.12's Unity has no HDR output
                Type t = Type.GetType("UnityEngine.HDROutputSettings, UnityEngine.CoreModule");
                if (t == null) return;
                HdrMain = t.GetProperty("main", BindingFlags.Static | BindingFlags.Public);
                HdrActive = t.GetProperty("active", BindingFlags.Instance | BindingFlags.Public);
            }
            catch { }
        }

        /// <summary>The game is sending HDR to the display.</summary>
        internal static bool HdrOutput
        {
            get
            {
                try
                {
                    if (HdrMain == null || HdrActive == null) return false;
                    object main = HdrMain.GetValue(null, null);
                    return main != null && (bool)HdrActive.GetValue(main, null);
                }
                catch { return false; }
            }
        }

        internal SceneLayer(bool persistent)
        {
            root = new GameObject("SeamlessSceneLayer");
            if (persistent) UnityEngine.Object.DontDestroyOnLoad(root);
            root.layer = Layer;
            root.transform.position = Place;

            cam = root.AddComponent<Camera>();
            cam.depth = Depth;
            cam.clearFlags = CameraClearFlags.Nothing;
            cam.cullingMask = 0;
            cam.orthographic = true;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 10f;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            cam.enabled = false;

            var cgo = new GameObject("FrozenCanvas", typeof(RectTransform));
            cgo.layer = Layer;
            cgo.transform.SetParent(root.transform, false);
            canvas = cgo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = 1f;
            canvas.enabled = false;
        }

        /// <summary>Where the frozen frame's image goes (set its layer with <see cref="Adopt"/>).</summary>
        internal Transform Parent => canvas.transform;

        internal void Adopt(GameObject image)
        {
            image.layer = Layer;
        }

        /// <summary>Call before the frame renders: the 3D picture of this frame is kept for
        /// <see cref="TakeCapture"/> at the end of it.</summary>
        internal void BeginCapture()
        {
            try
            {
                EndCapture();
                shot = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32)
                {
                    name = "Seamless frozen frame",
                    filterMode = FilterMode.Point
                };
                shot.Create();
                copy = new CommandBuffer { name = "Seamless frozen frame" };
                copy.Blit(BuiltinRenderTextureType.CurrentActive, shot);
                cam.AddCommandBuffer(CopyEvent, copy);
                cam.clearFlags = CameraClearFlags.Nothing;
                cam.cullingMask = 0;
                cam.enabled = true;
            }
            catch (Exception e)
            {
                Log.Warn("3D picture capture not started: " + e.Message);
                EndCapture();
            }
        }

        /// <summary>The picture <see cref="BeginCapture"/> asked for, or null (the caller then falls
        /// back to a screenshot).</summary>
        internal Texture2D TakeCapture()
        {
            if (shot == null) return null;
            RenderTexture before = RenderTexture.active;
            try
            {
                var tex = new Texture2D(shot.width, shot.height, TextureFormat.RGBA32, false);
                RenderTexture.active = shot;
                tex.ReadPixels(new Rect(0, 0, shot.width, shot.height), 0, 0, false);
                return tex;
            }
            catch (Exception e)
            {
                Log.Warn("3D picture capture: " + e.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = before;
                EndCapture();
            }
        }

        private void EndCapture()
        {
            if (copy != null)
            {
                try { cam.RemoveCommandBuffer(CopyEvent, copy); } catch { }
                copy.Release();
                copy = null;
            }
            if (shot != null)
            {
                shot.Release();
                UnityEngine.Object.Destroy(shot);
                shot = null;
            }
            cam.enabled = false;
        }

        /// <summary>Draws the frozen frame (or stops). Fully opaque it is the whole picture and the
        /// camera clears the screen for it, which also tells Retooled the frame has a 3D picture
        /// while no scene camera exists; fading, it blends over the scene's own picture.</summary>
        internal void Show(bool visible, bool opaque)
        {
            if (copy != null) return;   // capturing this frame
            canvas.enabled = visible;
            cam.cullingMask = visible ? 1 << Layer : 0;
            cam.clearFlags = visible && opaque ? CameraClearFlags.SolidColor : CameraClearFlags.Nothing;
            cam.backgroundColor = Color.black;
            cam.enabled = visible;
        }

        internal void Destroy()
        {
            try { EndCapture(); } catch { }
            if (root != null) UnityEngine.Object.Destroy(root);
        }
    }
}
