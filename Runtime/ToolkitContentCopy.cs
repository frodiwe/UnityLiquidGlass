using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityLiquidGlass
{
    /// A copy of one UI Toolkit glass button's own drawing (its text, icons, children), made by
    /// replaying their styles on a small offscreen panel at screen resolution. LiquidGlassPass draws
    /// it over the glass. Re-made only when something it shows changes.
    /// ponytail: backgrounds, borders and text are replayed; custom generateVisualContent and
    /// vector images are not.
    [AddComponentMenu("")]
    sealed class ToolkitContentCopy : MonoBehaviour
    {
        internal RenderTexture Texture => _ready ? _surface : null;
        internal Rect Crop;   // where the copy goes, panel px

        struct Paint { public VisualElement Element; public Rect Bounds, Clip; public float Opacity; }

        readonly List<Paint> _paints = new();
        readonly List<(VisualElement clip, VisualElement paint)> _pool = new();
        VisualElement _source, _replay;
        UIDocument _document;
        PanelSettings _settings;
        RenderTexture _surface;
        int _signature;
        bool _hasSignature, _ready;
        int _paintedFrame = -1;
        Rect _pendingCrop;

        internal static ToolkitContentCopy Bind(UIDocument owner, VisualElement button)
        {
            var go = new GameObject("Liquid glass content") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(owner.transform, false);
            var copy = go.AddComponent<ToolkitContentCopy>();
            copy._source = button;
            copy._settings = Instantiate(owner.panelSettings);
            return copy;
        }

        void LateUpdate()
        {
            if (_source?.panel == null) return;
            // A fresh paint shows up a frame later.
            if (_paintedFrame >= 0 && Time.frameCount > _paintedFrame) { Crop = _pendingCrop; _ready = true; _paintedFrame = -1; }
            var screen = _source.panel.visualTree.worldBound;
            if (screen.width <= 0) return;
            _paints.Clear();
            int hash = 17;
            var origin = Matrix4x4.Translate(new Vector3(_source.worldBound.x - _source.layout.x, _source.worldBound.y - _source.layout.y, 0f));
            Collect(_source, screen, 1f, origin, ref hash);
            bool first = true; var crop = default(Rect);
            foreach (var p in _paints)
            {
                var drawn = Intersect(p.Clip, p.Bounds);
                if (drawn.width <= 0 || drawn.height <= 0) continue;
                crop = first ? drawn : Rect.MinMaxRect(Mathf.Min(crop.xMin, drawn.xMin), Mathf.Min(crop.yMin, drawn.yMin),
                    Mathf.Max(crop.xMax, drawn.xMax), Mathf.Max(crop.yMax, drawn.yMax));
                first = false;
            }
            if (first) { _ready = false; return; }
            // On the screen's pixel grid, so the copy lands 1:1 and stays as crisp as the panel.
            float k = Screen.width / screen.width;
            crop = Rect.MinMaxRect(Mathf.Floor(crop.xMin * k) / k, Mathf.Floor(crop.yMin * k) / k,
                Mathf.Ceil(crop.xMax * k) / k, Mathf.Ceil(crop.yMax * k) / k);
            hash = HashCode.Combine(hash, crop, Screen.width);
            if (_hasSignature && hash == _signature) return;
            _hasSignature = true; _signature = hash;
            Prepare(crop, k);
            Replay(crop);
            _pendingCrop = crop; _paintedFrame = Time.frameCount;
        }

        void Collect(VisualElement e, Rect clip, float opacity, Matrix4x4 parent, ref int hash)
        {
            var style = e.resolvedStyle;
            if (e != _source) opacity *= style.opacity;   // the button's own fade is the glass's (LiquidGlassPass)
            if (style.display == DisplayStyle.None || style.visibility == Visibility.Hidden || opacity <= .001f) return;
            // From layout, translate and scale down the hierarchy: worldBound can disagree with
            // what is drawn under a scaled parent.
            var l = e.layout; var o = style.transformOrigin;
            var m = parent * Matrix4x4.Translate(new Vector3(l.x, l.y, 0f) + style.translate)
                * Matrix4x4.Translate(o) * Matrix4x4.Scale(style.scale.value) * Matrix4x4.Translate(-o);
            var a = m.MultiplyPoint3x4(Vector3.zero); var b = m.MultiplyPoint3x4(new Vector3(l.width, l.height, 0f));
            var bounds = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            if (e.style.overflow.value == Overflow.Hidden) clip = Intersect(clip, bounds);
            if (clip.width <= 0 || clip.height <= 0) return;
            var image = style.backgroundImage;
            // The button's own background and border are what the glass replaces: only its text stays.
            bool paints = e == _source ? e is TextElement : style.backgroundColor.a > 0 || image.texture != null || image.sprite != null || image.renderTexture != null
                || e is TextElement || style.borderTopWidth + style.borderBottomWidth + style.borderLeftWidth + style.borderRightWidth > 0;
            if (paints)
            {
                _paints.Add(new Paint { Element = e, Bounds = bounds, Clip = clip, Opacity = opacity });
                hash = HashCode.Combine(hash, e, bounds, clip, opacity, image, style.backgroundColor, style.unityBackgroundImageTintColor);
                hash = HashCode.Combine(hash, style.borderTopLeftRadius, style.borderTopColor, style.borderTopWidth, style.color, style.fontSize);
                if (e is TextElement t) hash = HashCode.Combine(hash, t.text);
                if (image.renderTexture != null) hash = HashCode.Combine(hash, Time.frameCount);   // a live texture: every frame
            }
            var children = e.hierarchy;
            for (int i = 0; i < children.childCount; i++) Collect(children[i], clip, opacity, m, ref hash);
        }

        void Prepare(Rect crop, float k)
        {
            // Exactly the screen pixels it covers, no mips: a resampled copy turns thin icons bold.
            int w = Mathf.Max(1, Mathf.RoundToInt(crop.width * k)), h = Mathf.Max(1, Mathf.RoundToInt(crop.height * k));
            if (_surface == null || _surface.width != w || _surface.height != h)
            {
                if (_surface != null) { _settings.targetTexture = null; _surface.Release(); Destroy(_surface); }
                _surface = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "Liquid glass content", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                _surface.Create();
                _ready = false;
            }
            if (_document == null)
            {
                _settings.name = "Liquid glass content panel";
                _settings.sortingOrder = -1000;
                _settings.scaleMode = PanelScaleMode.ConstantPixelSize;
                _settings.clearColor = true; _settings.colorClearValue = Color.clear;
                _settings.targetTexture = _surface;
                // A UIDocument nested under another shares its panel: this one has its own root.
                var go = new GameObject("Liquid glass content panel") { hideFlags = HideFlags.HideAndDontSave };
                go.SetActive(false);
                _document = go.AddComponent<UIDocument>();
                _document.panelSettings = _settings;
                go.SetActive(true);
                _replay = new VisualElement { pickingMode = PickingMode.Ignore };
                _replay.style.position = Position.Absolute; _replay.style.left = _replay.style.top = 0;
                _replay.style.overflow = Overflow.Hidden;
                _document.rootVisualElement.Add(_replay);
            }
            _settings.targetTexture = _surface;
            _settings.scale = w / crop.width;
            _replay.style.width = crop.width; _replay.style.height = crop.height;
        }

        void Replay(Rect crop)
        {
            for (int i = 0; i < _paints.Count; i++)
            {
                var p = _paints[i]; bool text = p.Element is TextElement;
                if (i >= _pool.Count || (_pool[i].paint is Label) != text)
                {
                    var clip = new VisualElement { pickingMode = PickingMode.Ignore };
                    clip.style.position = Position.Absolute; clip.style.overflow = Overflow.Hidden;
                    VisualElement paint = text ? new Label() : new VisualElement();
                    paint.pickingMode = PickingMode.Ignore; paint.style.position = Position.Absolute;
                    clip.Add(paint);
                    if (i < _pool.Count) { _pool[i].clip.RemoveFromHierarchy(); _pool[i] = (clip, paint); }
                    else _pool.Add((clip, paint));
                    _replay.Insert(i, clip);
                }
                var item = _pool[i]; item.clip.style.display = DisplayStyle.Flex;
                item.clip.style.left = p.Clip.x - crop.x; item.clip.style.top = p.Clip.y - crop.y;
                item.clip.style.width = p.Clip.width; item.clip.style.height = p.Clip.height;
                item.paint.style.left = p.Bounds.x - p.Clip.x; item.paint.style.top = p.Bounds.y - p.Clip.y;
                item.paint.style.width = p.Bounds.width; item.paint.style.height = p.Bounds.height;
                Copy(p.Element, item.paint, p.Opacity, skin: p.Element != _source);
            }
            for (int i = _paints.Count; i < _pool.Count; i++) _pool[i].clip.style.display = DisplayStyle.None;
        }

        static void Copy(VisualElement from, VisualElement to, float opacity, bool skin)
        {
            var r = from.resolvedStyle; var s = to.style;
            s.opacity = opacity;
            if (!skin)
            {
                s.backgroundColor = Color.clear; s.backgroundImage = StyleKeyword.None;
                s.borderLeftWidth = s.borderRightWidth = s.borderTopWidth = s.borderBottomWidth = 0;
            }
            else CopySkin(r, s);
            if (from is TextElement label && to is Label copy)
            {
                copy.text = label.text;
                copy.enableRichText = label.enableRichText;
                s.unityFont = r.unityFont; s.unityFontDefinition = r.unityFontDefinition;
                s.fontSize = r.fontSize; s.unityFontStyleAndWeight = r.unityFontStyleAndWeight;
                s.color = r.color; s.unityTextAlign = r.unityTextAlign; s.whiteSpace = r.whiteSpace;
                s.letterSpacing = r.letterSpacing; s.wordSpacing = r.wordSpacing; s.textShadow = from.style.textShadow;
                s.paddingLeft = r.paddingLeft; s.paddingRight = r.paddingRight;
                s.paddingTop = r.paddingTop; s.paddingBottom = r.paddingBottom;
                s.marginLeft = s.marginRight = s.marginTop = s.marginBottom = 0;
            }
        }

        static void CopySkin(IResolvedStyle r, IStyle s)
        {
            s.backgroundColor = r.backgroundColor;
            s.backgroundImage = new StyleBackground(r.backgroundImage);
            s.backgroundSize = r.backgroundSize; s.backgroundPositionX = r.backgroundPositionX;
            s.backgroundPositionY = r.backgroundPositionY; s.backgroundRepeat = r.backgroundRepeat;
            s.unityBackgroundImageTintColor = r.unityBackgroundImageTintColor;
            s.unitySliceLeft = r.unitySliceLeft; s.unitySliceRight = r.unitySliceRight;
            s.unitySliceTop = r.unitySliceTop; s.unitySliceBottom = r.unitySliceBottom; s.unitySliceScale = r.unitySliceScale;
            s.borderTopLeftRadius = r.borderTopLeftRadius; s.borderTopRightRadius = r.borderTopRightRadius;
            s.borderBottomLeftRadius = r.borderBottomLeftRadius; s.borderBottomRightRadius = r.borderBottomRightRadius;
            s.borderLeftWidth = r.borderLeftWidth; s.borderRightWidth = r.borderRightWidth;
            s.borderTopWidth = r.borderTopWidth; s.borderBottomWidth = r.borderBottomWidth;
            s.borderLeftColor = r.borderLeftColor; s.borderRightColor = r.borderRightColor;
            s.borderTopColor = r.borderTopColor; s.borderBottomColor = r.borderBottomColor;
        }

        static Rect Intersect(Rect a, Rect b) => Rect.MinMaxRect(Mathf.Max(a.xMin, b.xMin),
            Mathf.Max(a.yMin, b.yMin), Mathf.Min(a.xMax, b.xMax), Mathf.Min(a.yMax, b.yMax));

        void OnDestroy()
        {
            if (_document != null) Destroy(_document.gameObject);
            if (_surface != null) { _settings.targetTexture = null; _surface.Release(); Destroy(_surface); }
            if (_settings != null) Destroy(_settings);
        }
    }
}
