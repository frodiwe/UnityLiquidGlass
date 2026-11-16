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
        /// Apple's two glass variants (iOS 26 .glassEffect).
        public enum Style { Clear, Regular }

        /// How a glass shape looks: what Apple's glass offers, its two variants, the matte between them
        /// and a tint. Every other number is Apple's, read from its running glassBackground filter in the
        /// iOS 26 simulator (Apply) and, for .regular, measured constants in LiquidGlass.cginc.
        [Serializable]
        public sealed class Look
        {
            [Tooltip("0 = Apple's .clear, 1 = .regular (whiter, blurred more, a soft shadow, small glass turns dark over dark backdrops).")]
            [Range(0, 1)] public float matte = 0f;
            [Tooltip("Colour over what the glass shows, as Apple's .tint(); alpha is its strength, 0 = none. sRGB.")]
            public Color tint = new Color(1, 1, 1, 0);

            public static Look Clear => new Look();
            public static Look Regular => new Look { matte = 1f };
            public static Look Of(Style style) => style == Style.Regular ? Regular : Clear;
        }

        public const string ButtonClass = "liquid-glass";

        /// Makes a UI Toolkit element a glass button: the element's own drawing is hidden (it still
        /// takes the taps), the glass is drawn in its place and its content (text, icons, children)
        /// on top. `cornerRadius` in the panel's units, 0 = pill or circle. `look`: null = Look.Clear.
        public static void Toolkit(VisualElement element, float cornerRadius = 0f, Look look = null) => Toolkit(element, cornerRadius, null, look);

        /// The same, with the glass in the outline of `shape` (its alpha), stretched over the element.
        public static void Toolkit(VisualElement element, Sprite shape, Look look = null) => Toolkit(element, 0f, shape, look);

        /// A Toolkit glass element's look; change its fields, or set a new one, at any time.
        public static Look GetLook(VisualElement element) => Shapes.TryGetValue(element, out var v) ? v.look : null;
        public static void SetLook(VisualElement element, Look look)
        {
            if (element == null || !Shapes.TryGetValue(element, out var v)) return;
            Shapes[element] = (v.radius, v.shape, look ?? Look.Clear);
            LiquidGlassPass.SetLook(element, Shapes[element].look);
        }

        static void Toolkit(VisualElement element, float cornerRadius, Sprite shape, Look look)
        {
            if (element == null || LiquidGlassPass.Has(element)) return;
            look ??= Look.Clear;
            for (var p = element.parent; p != null; p = p.parent) if (p.ClassListContains(ButtonClass)) return;
            element.AddToClassList(ButtonClass);
            Shapes[element] = (cornerRadius, shape, look);
            if (element.panel == null) { element.RegisterCallback<AttachToPanelEvent>(OnAttached); return; }
            UIDocument owner = null;
            foreach (var doc in UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude))
                if (doc.rootVisualElement?.panel == element.panel) { owner = doc; break; }
            if (owner == null) { Debug.LogWarning($"[LiquidGlass] no UIDocument draws '{element.name}'"); return; }
            var copy = ToolkitContentCopy.Bind(owner, element);
            // Its own drawing goes (it still takes the taps): fade a parent to fade the glass.
            // Not an opacity filter: its shader is stripped from builds that use no filters.
            element.style.opacity = 0f;
            LiquidGlassPass.Add(new LiquidGlassPass.ToolkitItem { Element = element, Radius = cornerRadius, Shape = shape, Copy = copy, Look = look });
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

        static readonly Dictionary<VisualElement, (float radius, Sprite shape, Look look)> Shapes = new();

        static void OnAttached(AttachToPanelEvent e)
        {
            if (e.target is not VisualElement element) return;
            element.UnregisterCallback<AttachToPanelEvent>(OnAttached);
            element.RemoveFromClassList(ButtonClass);
            var (r, sh, lk) = Shapes.TryGetValue(element, out var v) ? v : (0f, null, null);
            Toolkit(element, r, sh, lk);
        }

        static void OnDetached(DetachFromPanelEvent e)
        {
            if (e.target is not VisualElement element) return;
            LiquidGlassPass.Remove(element);
            element.UnregisterCallback<DetachFromPanelEvent>(OnDetached);
            element.RegisterCallback<AttachToPanelEvent>(OnAttached);
        }

        internal static void Apply(Material m, Look look)
        {
            look ??= Look.Clear;
            // Apple's .clear, read from its glassBackground filter: a 60 x 20 pt quarter-circle bezel (at most 0.36
            // of a small shape's shorter side), blur sigma 1.65 pt and half of it at the outline, tone 0.075 + 1.04 c,
            // a 0.78 hairline lit from the top left. matte blends into .regular, whose numbers are measured
            // constants in the shader (bezel, blur, tone, shadow, the dark variant of small shapes).
            float matte = Mathf.Clamp01(look.matte), angle = 135f * Mathf.Deg2Rad;
            m.SetFloat("_Liquid", 1f);
            m.SetFloat("_BezelDepth", 60f);
            m.SetFloat("_BezelWidth", 20f);
            m.SetFloat("_BezelFraction", .36f);
            m.SetFloat("_LensStrength", 1f);
            m.SetFloat("_Dispersion", 0f);
            m.SetFloat("_Specular", .78f);
            m.SetFloat("_Lift", .075f);
            m.SetFloat("_Gain", 1.04f);
            m.SetFloat("_Saturation", 1f);
            m.SetFloat("_FrostEdge", .5f);
            m.SetFloat("_FrostRadius", 1.65f);
            m.SetFloat("_Matte", matte);
            m.SetFloat("_Highlight", .13f * matte);   // .regular's capsule highlight
            m.SetVector("_LightDir", new Vector4(Mathf.Cos(angle), Mathf.Sin(angle)));
            // SetVector, not SetColor: the shader tones sRGB-encoded values and the tint is one of them.
            m.SetVector("_Tint", new Vector4(look.tint.r, look.tint.g, look.tint.b, Mathf.Clamp01(look.tint.a)));
        }
    }
}
