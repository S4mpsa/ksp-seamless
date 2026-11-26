using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Seamless
{
    /// <summary>What the overlay should show this frame (screen pixels, origin bottom-left).</summary>
    internal struct OverlayFrame
    {
        public float Bars;          // 0..1 letterbox slide
        public bool PlanetOnScreen;
        public Vector2 Planet;      // centre
        public float PlanetRadius;
        public float Line;          // 0..1 leader line drawn
        public float TitleAlpha, DetailAlpha;
        public bool MarkerOnScreen;
        public Vector2 Marker;
        public float MarkerAlpha;
        public bool LowerThird;     // text in the lower left corner instead of on a leader line
        public float FrozenAlpha;   // frozen frame (loading screens) over the scene, under the bars
        public float FrozenZoom;    // >= 1, slow push-in about FrozenPivot (0..1 screen)
        public Vector2 FrozenPivot;
    }

    /// <summary>
    /// Letterbox bars, leader line, planet name + orbit text and the vessel marker, on a
    /// screen-space-overlay canvas of its own (stays visible while the game UI is hidden).
    /// </summary>
    internal sealed class Overlay
    {
        private readonly GameObject root;
        private readonly Texture2D ringTex;
        private readonly RectTransform top, bottom, seg1, seg2, marker;
        private readonly Image seg1Img, seg2Img, markerImg;
        private readonly TextMeshProUGUI title, details, markerLabel;
        private readonly RawImage frozen;
        private Texture2D frozenTex;
        // with HDR output the frozen frame is drawn as part of the 3D picture (see SceneLayer)
        private readonly SceneLayer layer;
        private readonly float barFraction;
        private int sizedForHeight = -1;

        public Overlay(Settings s, string titleText, string detailText, string vesselName, bool persistent = false)
        {
            barFraction = s.barFraction;
            root = new GameObject("SeamlessOverlay");
            if (persistent) Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;

            if (persistent && SceneLayer.HdrOutput) layer = new SceneLayer(true);
            var fgo = new GameObject("FrozenFrame", typeof(RectTransform));
            fgo.transform.SetParent(layer != null ? layer.Parent : root.transform, false);
            if (layer != null) layer.Adopt(fgo);
            frozen = fgo.AddComponent<RawImage>();
            frozen.raycastTarget = false;
            frozen.color = new Color(1, 1, 1, 0);
            var frt = frozen.rectTransform;
            frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one;
            frt.offsetMin = Vector2.zero; frt.offsetMax = Vector2.zero;
            fgo.SetActive(false);

            top = Box("TopBar", Color.black, out _);
            top.anchorMin = new Vector2(0, 1); top.anchorMax = new Vector2(1, 1); top.pivot = new Vector2(0.5f, 1);
            top.anchoredPosition = Vector2.zero; top.sizeDelta = new Vector2(0, 0);
            bottom = Box("BottomBar", Color.black, out _);
            bottom.anchorMin = new Vector2(0, 0); bottom.anchorMax = new Vector2(1, 0); bottom.pivot = new Vector2(0.5f, 0);
            bottom.anchoredPosition = Vector2.zero; bottom.sizeDelta = new Vector2(0, 0);

            seg1 = Box("Leader1", Color.white, out seg1Img);
            seg1.pivot = new Vector2(0, 0.5f);
            seg2 = Box("Leader2", Color.white, out seg2Img);
            seg2.pivot = new Vector2(0, 0.5f);
            seg2.localRotation = Quaternion.Euler(0, 0, 45f);

            ringTex = MakeRing(64);
            marker = Box("VesselMarker", Color.white, out markerImg);
            markerImg.sprite = Sprite.Create(ringTex, new Rect(0, 0, ringTex.width, ringTex.height), new Vector2(0.5f, 0.5f));
            marker.pivot = new Vector2(0.5f, 0.5f);

            title = Text("Title", titleText, Fonts.Bold, Color.white);
            if (title.font == Fonts.GameFont()) title.fontStyle = FontStyles.Bold;   // Orbitron missing
            details = Text("Details", detailText, Fonts.Medium, DetailColor);
            markerLabel = Text("VesselName", vesselName, Fonts.Medium, DetailColor);
            markerLabel.alignment = TextAlignmentOptions.MidlineLeft;
            markerLabel.rectTransform.pivot = new Vector2(0, 0.5f);

            Apply(new OverlayFrame());
        }

        public void Destroy()
        {
            if (root != null) Object.Destroy(root);
            if (layer != null) layer.Destroy();
            if (ringTex != null) Object.Destroy(ringTex);
            if (frozenTex != null) Object.Destroy(frozenTex);
        }

        /// <summary>Shows a captured frame over the scene (the overlay takes ownership).</summary>
        public void SetFrozen(Texture2D tex)
        {
            if (frozenTex != null && frozenTex != tex) Object.Destroy(frozenTex);
            frozenTex = tex;
            if (tex != null) tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            frozen.texture = tex;
            frozen.gameObject.SetActive(tex != null);
        }

        /// <summary>Call before the frame to freeze renders. With HDR output its 3D picture is then
        /// kept for <see cref="TakeSceneCapture"/>; otherwise nothing happens.</summary>
        public void BeginSceneCapture()
        {
            if (layer != null) layer.BeginCapture();
        }

        /// <summary>The 3D picture of the frame that just rendered, without the bars and the card,
        /// or null to freeze a screenshot instead.</summary>
        public Texture2D TakeSceneCapture()
        {
            return layer != null ? layer.TakeCapture() : null;
        }

        /// <summary>The frozen frame is drawn under the game's UI cameras instead of over everything.</summary>
        public bool FrozenUnderUi => layer != null;

        public void SetTexts(string titleText, string detailText)
        {
            title.text = titleText;
            details.text = detailText;
        }

        public void Apply(OverlayFrame f)
        {
            float W = Screen.width, H = Screen.height;
            if (sizedForHeight != Screen.height) Resize(H);

            float barH = barFraction * H * Ease(f.Bars);
            top.sizeDelta = new Vector2(0, barH);
            bottom.sizeDelta = new Vector2(0, barH);

            if (frozenTex != null)
            {
                float z = Mathf.Max(1f, f.FrozenZoom), w = 1f / z;
                frozen.uvRect = new Rect(f.FrozenPivot.x * (1f - w), f.FrozenPivot.y * (1f - w), w, w);
                frozen.color = new Color(1, 1, 1, Mathf.Clamp01(f.FrozenAlpha));
            }
            if (layer != null) layer.Show(frozenTex != null && f.FrozenAlpha > 0f, f.FrozenAlpha >= 1f);

            if (f.LowerThird)
            {
                SetAlpha(seg1Img, 0f);
                SetAlpha(seg2Img, 0f);
                // Title baseline at a fixed height, details hanging below it (top-aligned), so the
                // layout never depends on TextMeshPro's cached text measurements.
                float lx = 0.06f * W;
                float titleY = barFraction * H + 0.17f * H;
                float sl = 0.012f * H;
                SetLowerThirdAnchors(true);
                title.rectTransform.anchoredPosition = new Vector2(lx - 0.002f * H, titleY - sl * (1f - Ease(f.TitleAlpha)));
                title.alpha = f.TitleAlpha;
                details.rectTransform.anchoredPosition = new Vector2(lx, titleY - 0.012f * H - sl * (1f - Ease(f.DetailAlpha)));
                details.alpha = f.DetailAlpha;
                ApplyMarker(f, H);
                return;
            }

            SetLowerThirdAnchors(false);
            bool show = f.PlanetOnScreen;
            float th = Mathf.Max(2f, 0.0022f * H);

            // leader: from just right of the limb, horizontally, then 45 degrees up to the text
            float gap = 0.018f * H;
            float x0 = f.Planet.x + f.PlanetRadius + gap, y0 = f.Planet.y;
            float l1 = 0.12f * W, l2 = 0.14f * H;
            float l2x = l2 * 0.70710678f;
            float textW = Mathf.Max(title.preferredWidth, details.preferredWidth);
            float textLeftPad = 0.012f * H;
            float over = x0 + l1 + l2x + textLeftPad + textW - (W - 0.02f * W);
            if (over > 0) l1 = Mathf.Max(0.02f * W, l1 - over);

            float p1 = Mathf.Clamp01(f.Line * 2f), p2 = Mathf.Clamp01(f.Line * 2f - 1f);
            seg1.anchoredPosition = new Vector2(x0, y0);
            seg1.sizeDelta = new Vector2(l1 * Ease(p1), th);
            seg2.anchoredPosition = new Vector2(x0 + l1, y0);
            seg2.sizeDelta = new Vector2(l2 * Ease(p2), th);
            SetAlpha(seg1Img, show && p1 > 0 ? 1f : 0f);
            SetAlpha(seg2Img, show && p2 > 0 ? 1f : 0f);

            float ex = x0 + l1 + l2x, ey = y0 + l2x;
            float dA = show ? f.DetailAlpha : 0f, tA = show ? f.TitleAlpha : 0f;
            float slide = 0.012f * H;
            details.rectTransform.anchoredPosition = new Vector2(ex + textLeftPad, ey + 0.004f * H - slide * (1f - Ease(dA)));
            details.alpha = dA;
            float detailsH = details.preferredHeight;
            title.rectTransform.anchoredPosition = new Vector2(ex + textLeftPad - 0.002f * H, ey + 0.004f * H + detailsH + 0.004f * H - slide * (1f - Ease(tA)));
            title.alpha = tA;

            ApplyMarker(f, H);
        }

        private bool lowerThirdAnchors;

        /// <summary>Lower third: details pivot/alignment top-left (hanging under the title);
        /// leader layout: bottom-left (stacked on the leader line's end).</summary>
        private void SetLowerThirdAnchors(bool on)
        {
            if (on == lowerThirdAnchors) return;
            lowerThirdAnchors = on;
            details.rectTransform.pivot = on ? new Vector2(0, 1) : Vector2.zero;
            details.alignment = on ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.BottomLeft;
        }

        /// <summary>One log line describing the card (for diagnosing an invisible card).</summary>
        public string Describe()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string first = details.text ?? "";
            int nl = first.IndexOf('\n');
            if (nl >= 0) first = first.Substring(0, nl);
            return string.Format(inv,
                "card: title '{0}' a={1:F2} at {2} | details '{3}...' a={4:F2} at {5} size {6:F0}px font {7} active={8} | screen {9}x{10}",
                title.text, title.alpha, title.rectTransform.anchoredPosition, first, details.alpha,
                details.rectTransform.anchoredPosition, details.fontSize, details.font != null ? details.font.name : "null",
                details.isActiveAndEnabled, Screen.width, Screen.height);
        }

        private void ApplyMarker(OverlayFrame f, float H)
        {
            float mA = f.MarkerOnScreen ? f.MarkerAlpha : 0f;
            marker.anchoredPosition = f.Marker;
            SetAlpha(markerImg, mA);
            markerLabel.rectTransform.anchoredPosition = f.Marker + new Vector2(0.018f * H, 0);
            markerLabel.alpha = mA * 0.85f;
        }

        private void Resize(float H)
        {
            sizedForHeight = Screen.height;
            title.fontSize = 0.036f * H;
            title.characterSpacing = 4f;
            details.fontSize = 0.0175f * H;
            details.characterSpacing = 4f;
            details.lineSpacing = 18f;
            markerLabel.fontSize = 0.014f * H;
            markerLabel.characterSpacing = 2f;
            float s = 0.024f * H;
            marker.sizeDelta = new Vector2(s, s);
            Vector2 big = new Vector2(Screen.width, H * 0.3f);
            title.rectTransform.sizeDelta = big;
            details.rectTransform.sizeDelta = big;
            markerLabel.rectTransform.sizeDelta = new Vector2(Screen.width * 0.4f, 0.05f * H);
        }

        private RectTransform Box(string name, Color c, out Image img)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(root.transform, false);
            img = go.AddComponent<Image>();
            img.color = c;
            img.raycastTarget = false;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            return rt;
        }

        private static readonly Color DetailColor = new Color(0.74f, 0.76f, 0.80f, 1f);

        private TextMeshProUGUI Text(string name, string s, TMP_FontAsset font, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(root.transform, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            if (font != null) t.font = font;
            t.text = s;
            t.color = color;
            t.enableWordWrapping = false;
            t.alignment = TextAlignmentOptions.BottomLeft;
            t.raycastTarget = false;
            var rt = t.rectTransform;
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            t.alpha = 0f;
            return t;
        }

        private static void SetAlpha(Graphic g, float a)
        {
            Color c = g.color; c.a = a; g.color = c;
        }

        public static float Ease(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }

        private static Texture2D MakeRing(int n)
        {
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            var px = new Color32[n * n];
            float c = (n - 1) * 0.5f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float r = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / (n * 0.5f);
                    // ring between 0.72 and 0.95 of the radius, 1.5 px soft edges
                    float e = 1.5f / (n * 0.5f);
                    float a = Mathf.Clamp01((r - 0.72f) / e + 0.5f) * Mathf.Clamp01((0.95f - r) / e + 0.5f);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }
    }
}
