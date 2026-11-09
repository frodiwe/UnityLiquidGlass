using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityLiquidGlass
{
    /// Apple's iOS 26 Liquid Glass for Unity UI. uGUI: add a LiquidGlassButton. UI Toolkit: call
    /// LiquidGlass.Toolkit(element). Every glass shape is drawn by one last pass over the finished
    /// frame (LiquidGlassPass), so it refracts exactly what is on screen under it, with no frame of delay.
    public static class LiquidGlass
    {
        /// The look of every glass shape. Change the fields at any time; the next frame uses them.
        /// Defaults are matched against Apple's .glassEffect on real UI. Lengths are in the UI's own
        /// units: the canvas's (uGUI) or the panel's (UI Toolkit), as iOS's points.
        [Serializable]
        public sealed class Look
        {
            [Tooltip("0 = a flat tinted pane, 1 = liquid glass.")] public float liquid = 1f;
            [Tooltip("How far in the outline shows the backdrop, points; capped at the shape's shorter side (Apple: 60).")] public float bezelDepth = 60f;
            [Tooltip("The bezel's width along the outline, points; the middle inside it is flat (Apple: 20).")] public float bezelWidth = 20f;
            [Tooltip("On small shapes the bezel is at most this share of the shorter side (Apple: 0.36).")] public float bezelFraction = .36f;
            [Tooltip("Multiplier on the whole lens (0 = flat glass).")] public float lensStrength = 1f;
            [Tooltip("Colour split where the glass bends (0 = none, as Apple's).")] public float dispersion = 0f;
            [Tooltip("The blur of what the glass shows: Gaussian sigma, points (Apple's .clear: 1.65).")] public float frostRadius = 1.65f;
            [Tooltip("How much lighter the blur is at the outline than in the middle (Apple: 0.5).")] public float frostEdge = .5f;
            [Tooltip("Added to every channel of what the glass shows, sRGB (Apple's .clear: 0.075).")] public float lift = .075f;
            [Tooltip("What the glass shows is multiplied by this, sRGB (Apple's .clear: 1.04).")] public float gain = 1.04f;
            [Tooltip("Colour strength of what the glass shows (1 = unchanged, as Apple's .clear).")] public float saturation = 1f;
            [Tooltip("The hairline highlight on the outline (0 = none).")] public float specular = .78f;
            [Tooltip("Where the hairline's key light comes from, degrees (0 = right, 90 = top); a fill light shines from the opposite side.")] public float lightAngle = 135f;
            [Tooltip("Apple's Clear / Tinted switch as a slider: 0 = .clear, 1 = .regular (whiter, softer, a highlight on capsules).")] public float matte = 0f;
            [Tooltip("The soft highlight a .regular capsule catches over its left cap (scaled by matte).")] public float highlight = .13f;
        }

        public static Look Settings = new();

        public const string ButtonClass = "liquid-glass";

        /// Makes a UI Toolkit element a glass button: the element's own drawing is hidden (it still
        /// takes the taps), the glass is drawn in its place and its content (text, icons, children)
        /// on top. `cornerRadius` in the panel's units, 0 = pill or circle.
        public static void Toolkit(VisualElement element, float cornerRadius = 0f) => Toolkit(element, cornerRadius, null);

        /// The same, with the glass in the outline of `shape` (its alpha), stretched over the element.
        public static void Toolkit(VisualElement element, Sprite shape) => Toolkit(element, 0f, shape);

        static void Toolkit(VisualElement element, float cornerRadius, Sprite shape)
        {
            if (element == null || LiquidGlassPass.Has(element)) return;
            for (var p = element.parent; p != null; p = p.parent) if (p.ClassListContains(ButtonClass)) return;
            element.AddToClassList(ButtonClass);
            Shapes[element] = (cornerRadius, shape);
            if (element.panel == null) { element.RegisterCallback<AttachToPanelEvent>(OnAttached); return; }
            UIDocument owner = null;
            foreach (var doc in UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude))
                if (doc.rootVisualElement?.panel == element.panel) { owner = doc; break; }
            if (owner == null) { Debug.LogWarning($"[LiquidGlass] no UIDocument draws '{element.name}'"); return; }
            var copy = ToolkitContentCopy.Bind(owner, element);
            // Its own drawing goes (it still takes the taps): fade a parent to fade the glass.
            // Not an opacity filter: its shader is stripped from builds that use no filters.
            element.style.opacity = 0f;
            LiquidGlassPass.Add(new LiquidGlassPass.ToolkitItem { Element = element, Radius = cornerRadius, Shape = shape, Copy = copy });
            element.RegisterCallback<DetachFromPanelEvent>(OnDetached);
        }

        /// The element stops being glass and draws itself again.
        public static void Remove(VisualElement element)
        {
            if (element == null) return;
            LiquidGlassPass.Remove(element);
            Shapes.Remove(element);
            element.RemoveFromClassList(ButtonClass);
            element.style.opacity = StyleKeyword.Null;
            element.UnregisterCallback<AttachToPanelEvent>(OnAttached);
            element.UnregisterCallback<DetachFromPanelEvent>(OnDetached);
        }

        static readonly Dictionary<VisualElement, (float radius, Sprite shape)> Shapes = new();

        static void OnAttached(AttachToPanelEvent e)
        {
            if (e.target is not VisualElement element) return;
            element.UnregisterCallback<AttachToPanelEvent>(OnAttached);
            element.RemoveFromClassList(ButtonClass);
            var (r, sh) = Shapes.TryGetValue(element, out var v) ? v : (0f, null);
            Toolkit(element, r, sh);
        }

        static void OnDetached(DetachFromPanelEvent e)
        {
            if (e.target is not VisualElement element) return;
            LiquidGlassPass.Remove(element);
            element.UnregisterCallback<DetachFromPanelEvent>(OnDetached);
            element.RegisterCallback<AttachToPanelEvent>(OnAttached);
        }

        internal static void Apply(Material m)
        {
            var s = Settings;
            float matte = Mathf.Clamp01(s.matte), angle = s.lightAngle * Mathf.Deg2Rad;
            m.SetFloat("_Liquid", Mathf.Clamp01(s.liquid));
            m.SetFloat("_BezelDepth", Mathf.Max(0f, s.bezelDepth));
            m.SetFloat("_BezelWidth", Mathf.Max(.01f, s.bezelWidth));
            m.SetFloat("_BezelFraction", Mathf.Max(.01f, s.bezelFraction));
            m.SetFloat("_LensStrength", s.lensStrength);
            m.SetFloat("_Dispersion", s.dispersion);
            m.SetFloat("_Specular", Mathf.Max(0f, s.specular));
            m.SetFloat("_Lift", s.lift);
            m.SetFloat("_Gain", Mathf.Max(0f, s.gain));
            m.SetFloat("_Saturation", Mathf.Max(0f, s.saturation));
            m.SetFloat("_FrostEdge", Mathf.Clamp01(s.frostEdge));
            // matte blends .clear into Apple's .regular, whose numbers are measured constants in the shader
            // (bezel, blur, tone, shadow, and the dark variant of small shapes over dark backdrops).
            m.SetFloat("_Matte", matte);
            m.SetFloat("_FrostRadius", Mathf.Max(0f, s.frostRadius));
            m.SetFloat("_Highlight", Mathf.Max(0f, s.highlight) * matte);
            m.SetVector("_LightDir", new Vector4(Mathf.Cos(angle), Mathf.Sin(angle)));
        }
    }
}
