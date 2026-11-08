using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.UIElements;
using UnityLiquidGlass;
using Image = UnityEngine.UI.Image;

/// Add to an empty GameObject in an empty scene and press Play. Builds a moving, colourful backdrop,
/// uGUI glass buttons (drag the capsule around) and a UI Toolkit glass button that switches the
/// glass between Apple's .clear and .regular looks.
public sealed class LiquidGlassDemo : MonoBehaviour
{
    RawImage _backdrop;

    void Start()
    {
        if (Camera.main == null) new GameObject("Camera").AddComponent<Camera>().tag = "MainCamera";
        if (FindAnyObjectByType<EventSystem>() == null)
        {
            var es = new GameObject("EventSystem").AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            es.gameObject.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
            es.gameObject.AddComponent<StandaloneInputModule>();
#endif
        }
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        var canvas = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster)).GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvas.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(430, 932);
        scaler.matchWidthOrHeight = 1;

        _backdrop = Node<RawImage>("Backdrop", canvas.transform, Vector2.zero, Vector2.zero);
        _backdrop.rectTransform.anchorMin = Vector2.zero; _backdrop.rectTransform.anchorMax = Vector2.one;
        _backdrop.texture = Backdrop();
        _backdrop.raycastTarget = false;

        // A circle with an ✕ drawn by two thin bars.
        var close = Glass("Close", canvas.transform, new Vector2(150, 360), new Vector2(56, 56), 0);
        foreach (var angle in new[] { 45f, -45f })
        {
            var bar = Node<Image>("Bar", close.transform, Vector2.zero, new Vector2(24, 3));
            bar.color = new Color(.1f, .1f, .15f);
            bar.rectTransform.localRotation = Quaternion.Euler(0, 0, angle);
        }
        // A capsule with a label; drag it over the backdrop.
        var pill = Glass("Pill", canvas.transform, new Vector2(0, 120), new Vector2(240, 56), 0);
        Label(pill.transform, font, "Drag me", 22);
        pill.gameObject.AddComponent<Drag>();
        // A rounded card.
        var card = Glass("Card", canvas.transform, new Vector2(0, -60), new Vector2(330, 130), 28);
        Label(card.transform, font, "Liquid Glass\nfor Unity", 26);
        // Any outline: the glass follows a sprite's alpha (here a heart, made in code).
        var heart = Glass("Heart", canvas.transform, new Vector2(-110, 330), new Vector2(120, 110), 0);
        heart.shape = Heart();
        Label(heart.transform, font, "shape", 20);

        Toolkit(font);
    }

    void Update()
    {
        // The backdrop drifts, so the refraction is visibly live.
        var r = _backdrop.uvRect; r.y = Time.time * .02f % 1f; _backdrop.uvRect = r;
    }

    void Toolkit(Font font)
    {
        var settings = ScriptableObject.CreateInstance<PanelSettings>();
        settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
        settings.referenceResolution = new Vector2Int(430, 932);
        settings.match = 1;
        settings.sortingOrder = 10;
        var doc = new GameObject("UI Toolkit").AddComponent<UIDocument>();
        doc.panelSettings = settings;
        var root = doc.rootVisualElement;
        var button = new UnityEngine.UIElements.Button(() =>
            LiquidGlass.Settings.matte = LiquidGlass.Settings.matte > .5f ? 0f : 1f) { text = "UI Toolkit · tap: clear / regular" };
        var s = button.style;
        s.position = Position.Absolute; s.width = 330; s.height = 56; s.top = 700; s.left = 50;
        s.fontSize = 18; s.unityTextAlign = TextAnchor.MiddleCenter; s.color = new Color(.1f, .1f, .15f);
        s.unityFontDefinition = FontDefinition.FromFont(font);
        root.Add(button);
        LiquidGlass.Toolkit(button);
    }

    static LiquidGlassButton Glass(string name, Transform parent, Vector2 pos, Vector2 size, float radius)
    {
        var image = Node<Image>(name, parent, pos, size);
        image.color = Color.white;   // any colour: the glass replaces it
        var glass = image.gameObject.AddComponent<LiquidGlassButton>();
        glass.cornerRadius = radius;
        return glass;
    }

    static void Label(Transform parent, Font font, string text, int size)
    {
        var t = Node<Text>("Label", parent, Vector2.zero, Vector2.zero);
        t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
        t.font = font; t.text = text; t.fontSize = size; t.alignment = TextAnchor.MiddleCenter;
        t.color = new Color(.1f, .1f, .15f); t.raycastTarget = false;
    }

    static T Node<T>(string name, Transform parent, Vector2 pos, Vector2 size) where T : Component
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchoredPosition = pos; rt.sizeDelta = size;
        return go.AddComponent<T>();
    }

    static Sprite Heart()
    {
        const int w = 240, h = 220;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float u = (x - w / 2f) / (w * .42f), v = (y - h * .45f) / (h * .45f);   // y up: lobes at the top
            float f = Mathf.Pow(u * u + v * v - 1f, 3f) - u * u * v * v * v;
            px[y * w + x] = f < 0f ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
        }
        tex.SetPixels32(px); tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(.5f, .5f));
    }

    /// Bright blobs over a fine grid with diagonals: lines show the lens, colours show the rim.
    static Texture2D Backdrop()
    {
        const int w = 512, h = 1024;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
        var px = new Color32[w * h];
        var blobs = new[] { (x: .25f, y: .2f, c: new Color(1f, .35f, .3f)), (x: .8f, y: .45f, c: new Color(.25f, .55f, 1f)),
                            (x: .3f, y: .7f, c: new Color(1f, .8f, .2f)), (x: .75f, y: .9f, c: new Color(.3f, .85f, .5f)) };
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var c = new Color(.93f, .9f, .85f);
            foreach (var b in blobs)
            {
                float dx = x / (float)w - b.x, dy = (y / (float)h - b.y) * 2f;
                c = Color.Lerp(c, b.c, Mathf.Clamp01(1.2f - Mathf.Sqrt(dx * dx + dy * dy) * 6f));
            }
            if (x % 32 < 2 || y % 32 < 2) c = Color.Lerp(c, new Color(.12f, .12f, .25f), .85f);
            if ((x + y) % 80 < 2) c = Color.Lerp(c, new Color(.8f, .1f, .1f), .8f);
            px[y * w + x] = c;
        }
        tex.SetPixels32(px); tex.Apply(true);
        return tex;
    }

    sealed class Drag : MonoBehaviour, IDragHandler
    {
        public void OnDrag(PointerEventData e) => ((RectTransform)transform).anchoredPosition += e.delta / GetComponentInParent<Canvas>().scaleFactor;
    }
}
