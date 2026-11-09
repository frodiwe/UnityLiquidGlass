using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace UnityLiquidGlass
{
    /// Liquid glass as the frame's last pass.
    ///
    /// Every camera and UI panel draws as usual. A uGUI glass shape and its content draw only a marker
    /// there (Marker.shader: alpha 0, colour untouched). At the end of the frame the finished picture
    /// is copied, each shape is drawn over the copy with the lens (Glass.shader → LiquidGlass.cginc)
    /// and its content goes on top, into a layer that reaches the screen only where the markers
    /// survived: whatever the UI drew over a button (a popup) put its alpha back and hides it.
    /// UI Toolkit buttons have no marker: they are skipped when something else takes the taps at
    /// their centre (Covered).
    [AddComponentMenu("")]
    sealed class LiquidGlassPass : MonoBehaviour
    {
        internal sealed class ToolkitItem
        {
            public VisualElement Element;
            public float Radius;
            public Sprite Shape;
            public ToolkitContentCopy Copy;
        }

        static LiquidGlassPass _instance;
        static Material _marker;
        readonly List<LiquidGlassButton> _buttons = new();
        readonly Dictionary<LiquidGlassButton, List<Graphic>> _content = new();
        readonly List<ToolkitItem> _toolkit = new();
        RenderTexture _frame, _mipped;
        Material _glass, _layer;
        Mesh _fade;
        UIDocument[] _docs = System.Array.Empty<UIDocument>();
        float _docsAt;

        static readonly int GlassRect = Shader.PropertyToID("_GlassRect"), GlassShape = Shader.PropertyToID("_GlassShape"),
            GlassScreen = Shader.PropertyToID("_GlassScreen"), SharpTex = Shader.PropertyToID("_SharpTex"),
            MainTex = Shader.PropertyToID("_MainTex"), Premultiplied = Shader.PropertyToID("_Premultiplied"),
            ToLinear = Shader.PropertyToID("_ToLinear"), Fade = Shader.PropertyToID("_Fade"),
            LayerTex = Shader.PropertyToID("_Layer"), FrameTex = Shader.PropertyToID("_Frame"),
            LayerScreen = Shader.PropertyToID("_LayerScreen"), ShapeTex = Shader.PropertyToID("_ShapeTex"),
            ShapeUV = Shader.PropertyToID("_ShapeUV"), ShapeTexels = Shader.PropertyToID("_ShapeTexels"), GuiZTest = Shader.PropertyToID("unity_GUIZTestMode");
        static readonly Vector3[] Corners = new Vector3[4];
        static readonly List<Graphic> Scratch = new();
        static readonly List<Vector4> Uvs = new();

        internal static Material MarkerMaterial
        {
            get
            {
                if (_marker == null && Shader.Find("Hidden/LiquidGlass/Marker") is { } s)
                    _marker = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
                return _marker;
            }
        }

        static LiquidGlassPass Instance
        {
            get
            {
                if (_instance != null) return _instance;
                var go = new GameObject("Liquid glass pass") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                return _instance = go.AddComponent<LiquidGlassPass>();
            }
        }

        internal static void Add(LiquidGlassButton button)
        {
            var p = Instance;
            if (p._buttons.Contains(button)) return;
            p._buttons.Add(button);
            p._content[button] = new List<Graphic>();
            Mark(button.Shape, false);
        }

        internal static void Remove(LiquidGlassButton button)
        {
            if (_instance == null || !_instance._buttons.Remove(button)) return;
            Unmark(button.Shape);
            if (_instance._content.Remove(button, out var content)) foreach (var g in content) Unmark(g);
        }

        internal static void Add(ToolkitItem item)
        {
            var p = Instance;
            if (!Has(item.Element)) p._toolkit.Add(item);
        }

        internal static bool Has(VisualElement element)
        {
            if (_instance == null) return false;
            foreach (var t in _instance._toolkit) if (t.Element == element) return true;
            return false;
        }

        internal static void Remove(VisualElement element)
        {
            if (_instance == null) return;
            _instance._toolkit.RemoveAll(t =>
            {
                if (t.Element != element) return false;
                if (t.Copy != null) Destroy(t.Copy.gameObject);
                return true;
            });
        }

        static void Mark(Graphic g, bool content)
        {
            if (g == null) return;
            if (!g.TryGetComponent<LiquidGlassMarker>(out var m)) m = g.gameObject.AddComponent<LiquidGlassMarker>();
            m.hideFlags = HideFlags.DontSave; m.enabled = true;
            if (content && g is not TMP_Text && g is not TMP_SubMeshUI && !g.TryGetComponent<LiquidGlassContent>(out _))
                g.gameObject.AddComponent<LiquidGlassContent>().hideFlags = HideFlags.DontSave;
        }

        static void Unmark(Graphic g)
        {
            if (g == null) return;
            if (g.TryGetComponent<LiquidGlassMarker>(out var m)) { m.enabled = false; Destroy(m); }
            if (g.TryGetComponent<LiquidGlassContent>(out var c)) Destroy(c);
        }

        void OnEnable() => StartCoroutine(EveryFrame());

        void LateUpdate()
        {
            // Content added to a button later (a badge) gets its marker too. Custom shapes' fields
            // are built here, outside the frame pass.
            foreach (var t in _toolkit) ShapeField.Of(t.Shape);
            foreach (var b in _buttons)
            {
                ShapeField.Of(b.shape);
                var list = _content[b];
                b.GetComponentsInChildren(true, Scratch);
                foreach (var g in Scratch)
                    if (g != b.Shape && !list.Contains(g)) { list.Add(g); Mark(g, true); }
                list.RemoveAll(g => g == null);
            }
        }

        IEnumerator EveryFrame()
        {
            var end = new WaitForEndOfFrame();
            while (true) { yield return end; Draw(); }
        }

        bool Ready()
        {
            if (_glass == null && Shader.Find("Hidden/LiquidGlass/Glass") is { } g) _glass = new Material(g) { hideFlags = HideFlags.HideAndDontSave };
            if (_layer == null && Shader.Find("Hidden/LiquidGlass/Layer") is { } l) _layer = new Material(l) { hideFlags = HideFlags.HideAndDontSave };
            return _glass != null && _layer != null;
        }

        void Draw()
        {
            int w = Screen.width, h = Screen.height;
            if (w <= 0 || h <= 0 || !Ready()) return;
            _buttons.RemoveAll(b => b == null);
            _toolkit.RemoveAll(t => t.Element == null || t.Copy == null);
            bool ugui = false, any = false;
            foreach (var b in _buttons) if (Alpha(b) > .001f) { ugui = any = true; break; }
            if (!any) foreach (var t in _toolkit) if (Alpha(t.Element) > .001f) { any = true; break; }
            if (!any) return;

            if (_frame == null || _frame.width != w || _frame.height != h)
            {
                Release();
                // Linear on both sides: the copy keeps the screen's values and the glass writes them back the same way.
                _frame = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "Liquid glass frame" };
                _mipped = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                { name = "Liquid glass frame (mips)", useMipMap = true, autoGenerateMips = true, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
            }
            ScreenCapture.CaptureScreenshotIntoRenderTexture(_frame);
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            // The frost reads the mips, sRGB-encoded in both colour spaces (Layer pass 2).
            if (linear) Graphics.Blit(_frame, _mipped, _layer, 2);
            else Graphics.Blit(_frame, _mipped);
            LiquidGlass.Apply(_glass);
            _glass.SetTexture(SharpTex, _mipped);
            _glass.SetVector(GlassScreen, new Vector4(w, h, 0, 0));
            // The copy comes out upside down where the texture origin is the top (Metal, D3D, Vulkan).
            float flip = SystemInfo.graphicsUVStartsAtTop ? 1f : 0f;
            _layer.SetFloat(ToLinear, linear ? 1f : 0f);

            // Linear values in 8 bits lose the darkest tones (the glass over black came out 0.051, not 0.075).
            var layer = ugui ? RenderTexture.GetTemporary(w, h, 0, linear ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) : null;
            RenderTexture.active = layer;
            if (ugui) GL.Clear(false, true, Color.clear);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, w, 0, h);
            // UI shaders (TMP) take their depth test from this global; the last canvas may have left anything in it.
            Shader.SetGlobalInt(GuiZTest, (int)UnityEngine.Rendering.CompareFunction.Always);
            bool drew = false;
            if (ugui) foreach (var b in _buttons)
            {
                float alpha = Alpha(b);
                if (alpha <= .001f) continue;
                var r = ScreenRect(b.Shape.rectTransform);
                var canvas = b.Shape.canvas;
                float units = canvas != null ? canvas.rootCanvas.scaleFactor : 1f;   // screen px per canvas unit
                float inset = b.inset * units;
                r = Rect.MinMaxRect(r.xMin + inset, r.yMin + inset, r.xMax - inset, r.yMax - inset);
                if (r.width <= 0 || r.height <= 0) continue;
                Glass(r, units, b.cornerRadius, b.shape, alpha, flip);
                drew = true;
                foreach (var g in _content[b]) DrawContent(g);
            }
            RenderTexture.active = null;
            GL.LoadPixelMatrix(0, w, 0, h);
            if (drew)
            {
                _layer.SetTexture(LayerTex, layer);
                _layer.SetTexture(FrameTex, _frame);
                _layer.SetVector(LayerScreen, new Vector4(w, h, flip, 0));
                _layer.SetPass(1);
                Quad(new Rect(0, 0, w, h));
            }
            if (layer != null) RenderTexture.ReleaseTemporary(layer);

            foreach (var t in _toolkit)
            {
                float alpha = Alpha(t.Element);
                if (alpha <= .001f || Covered(t.Element)) continue;
                var panel = t.Element.panel;
                float toScreen = w / Mathf.Max(1f, panel.visualTree.worldBound.width);
                var r = PanelToScreen(t.Element.worldBound, toScreen, h);
                if (r.width <= 0 || r.height <= 0) continue;
                Glass(r, toScreen, t.Radius, t.Shape, alpha, flip);
                if (t.Copy.Texture != null && t.Copy.Crop.width > 0)
                    // Unity's own render texture reads right side up in a quad on every API; it holds straight alpha.
                    TexQuad(PanelToScreen(t.Copy.Crop, toScreen, h), t.Copy.Texture, alpha, premultiplied: false);
            }
            GL.PopMatrix();
        }

        void Glass(Rect r, float units, float radius, Sprite shape, float alpha, float flip)
        {
            if (shape != null && ShapeField.Of(shape) is { } field)
            {
                _glass.SetTexture(ShapeTex, field.Texture);
                _glass.SetVector(ShapeUV, field.Uv);
                _glass.SetVector(ShapeTexels, field.Texels);
                radius = -1f;
            }
            else radius = Mathf.Max(0f, radius);
            _glass.SetVector(GlassRect, new Vector4(r.center.x, r.center.y, r.width * .5f, r.height * .5f));
            _glass.SetVector(GlassShape, new Vector4(units, radius, alpha, flip));
            _glass.SetPass(0);
            Quad(r);
        }

        void DrawContent(Graphic g)
        {
            if (g == null || !g.isActiveAndEnabled) return;
            float a = g.canvasRenderer.GetInheritedAlpha();
            if (a <= .001f) return;
            if (g is TextMeshProUGUI text) DrawTmp(text.mesh, text.fontSharedMaterial, text.transform, a);
            else if (g is TMP_SubMeshUI sub) DrawTmp(sub.mesh, sub.sharedMaterial, sub.transform, a);
            else if (g.TryGetComponent<LiquidGlassContent>(out var c) && c.Mesh != null && c.Mesh.vertexCount > 0)
            {
                _layer.SetTexture(MainTex, g.mainTexture != null ? g.mainTexture : Texture2D.whiteTexture);
                _layer.SetFloat(Premultiplied, 0f);
                _layer.SetFloat(Fade, a);
                _layer.SetPass(0);
                Graphics.DrawMeshNow(c.Mesh, g.transform.localToWorldMatrix);
            }
        }

        // Its own font material: materialForRendering is the marker now.
        void DrawTmp(Mesh mesh, Material material, Transform t, float a)
        {
            if (mesh == null || mesh.vertexCount == 0 || material == null) return;
            if (a < .999f)
            {
                // A fading button: the text's vertex alpha scaled by the fade it inherits.
                if (_fade == null) _fade = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                _fade.Clear();
                _fade.SetVertices(mesh.vertices);
                // Vector4: TMP keeps the glyph's scale in uv0's zw; as Vector2 the text vanishes.
                for (int ch = 0; ch < 4; ch++) { mesh.GetUVs(ch, Uvs); if (Uvs.Count > 0) _fade.SetUVs(ch, Uvs); }
                var colors = mesh.colors32;
                for (int i = 0; i < colors.Length; i++) colors[i].a = (byte)(colors[i].a * a);
                _fade.SetColors(colors); _fade.SetTriangles(mesh.triangles, 0);
                mesh = _fade;
            }
            material.SetPass(0);
            Graphics.DrawMeshNow(mesh, t.localToWorldMatrix);
        }

        void TexQuad(Rect r, Texture tex, float alpha, bool premultiplied)
        {
            _layer.SetTexture(MainTex, tex);
            _layer.SetFloat(Premultiplied, premultiplied ? 1f : 0f);
            _layer.SetFloat(Fade, alpha);
            _layer.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(Color.white);
            GL.TexCoord2(0, 0); GL.Vertex3(r.xMin, r.yMin, 0);
            GL.TexCoord2(0, 1); GL.Vertex3(r.xMin, r.yMax, 0);
            GL.TexCoord2(1, 1); GL.Vertex3(r.xMax, r.yMax, 0);
            GL.TexCoord2(1, 0); GL.Vertex3(r.xMax, r.yMin, 0);
            GL.End();
        }

        static void Quad(Rect r)
        {
            GL.Begin(GL.QUADS);
            GL.Vertex3(r.xMin, r.yMin, 0); GL.Vertex3(r.xMin, r.yMax, 0);
            GL.Vertex3(r.xMax, r.yMax, 0); GL.Vertex3(r.xMax, r.yMin, 0);
            GL.End();
        }

        static Rect PanelToScreen(Rect r, float k, int h) => Rect.MinMaxRect(r.xMin * k, h - r.yMax * k, r.xMax * k, h - r.yMin * k);

        static Rect ScreenRect(RectTransform rt)
        {
            rt.GetWorldCorners(Corners);
            Vector2 a = RectTransformUtility.WorldToScreenPoint(null, Corners[0]);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(null, Corners[2]);
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        static float Alpha(LiquidGlassButton b)
        {
            var g = b.Shape;
            if (g == null || !b.isActiveAndEnabled || !g.isActiveAndEnabled) return 0f;
            return g.canvasRenderer.GetInheritedAlpha() * g.color.a;
        }

        /// The fades above the element (its own opacity is 0: LiquidGlass.Toolkit hides its drawing).
        static float Alpha(VisualElement e)
        {
            if (e?.panel == null) return 0f;
            var own = e.resolvedStyle;
            if (own.display == DisplayStyle.None || own.visibility == Visibility.Hidden) return 0f;
            float a = 1f;
            for (var p = e.hierarchy.parent; p != null && a > .001f; p = p.hierarchy.parent)
            {
                if (p.resolvedStyle.display == DisplayStyle.None) return 0f;
                a *= p.resolvedStyle.opacity;
            }
            return a;
        }

        /// Whether something else takes the taps at the element's centre: a popup on its own panel,
        /// or anything on a panel sorted above it.
        /// ponytail: one point, and uGUI over a panel is not seen; a marker per panel would cover both.
        bool Covered(VisualElement e)
        {
            var panel = e.panel;
            var c = e.worldBound.center;
            var top = panel.Pick(c);
            if (top != null && top != e && !e.Contains(top) && !top.Contains(e)) return true;
            if (Time.unscaledTime >= _docsAt) { _docsAt = Time.unscaledTime + .5f; _docs = FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude); }
            float order = float.MinValue;
            foreach (var d in _docs) if (d != null && d.rootVisualElement?.panel == panel && d.panelSettings != null) { order = d.panelSettings.sortingOrder; break; }
            float w = panel.visualTree.worldBound.width;
            foreach (var d in _docs)
            {
                var other = d != null ? d.rootVisualElement?.panel : null;
                if (other == null || other == panel || d.panelSettings == null || d.panelSettings.sortingOrder <= order) continue;
                if (other.Pick(c * (other.visualTree.worldBound.width / Mathf.Max(1f, w))) != null) return true;
            }
            return false;
        }

        void Release()
        {
            if (_frame != null) { _frame.Release(); Destroy(_frame); }
            if (_mipped != null) { _mipped.Release(); Destroy(_mipped); }
            _frame = _mipped = null;
        }

        void OnDestroy()
        {
            Release();
            if (_glass != null) Destroy(_glass);
            if (_layer != null) Destroy(_layer);
            if (_fade != null) Destroy(_fade);
        }
    }
}
