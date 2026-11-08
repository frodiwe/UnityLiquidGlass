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
            [Tooltip("How much the middle is enlarged (1 = not at all).")] public float magnify = 1f;
            [Tooltip("Where the outline samples, in half heights from the middle; below 0 = the far side, so the image folds back at the rim.")] public float rimReach = -1.5f;
            [Tooltip("How tight to the edge the fold sits (higher = thinner rim).")] public float rimPower = 4f;
            [Tooltip("Multiplier on the whole lens (0 = flat glass).")] public float lensStrength = 1f;
            [Tooltip("Colour split where the glass bends (0 = none, as Apple's).")] public float dispersion = 0f;
            [Tooltip("How much light scatters: 0 = clear, 1 = all of it blurred by frostRadius.")] public float frost = 1f;
            [Tooltip("The frost blur's radius, points.")] public float frostRadius = 2.7f;
            [Tooltip("The backdrop's own blur before the lens, relative to frostRadius.")] public float sourceBlur = .5f;
            [Tooltip("How much blurred detail stands out from the glass around it.")] public float contrast = 1.3f;
            [Tooltip("Colour strength of what the glass shows (vibrancy).")] public float saturation = 1.3f;
            [Tooltip("What the glass shows is this much brighter.")] public float brightness = 1.1f;
            [Tooltip("White mixed over the glass (0 = clear).")] public float tint = .04f;
            [Tooltip("Light on the squeezed rim and the hairline on the outline.")] public float specular = 1.4f;
            [Tooltip("Where the light comes from, degrees (0 = right, 90 = top).")] public float lightAngle = 135f;
            [Tooltip("The rim's light takes the colour under the edge; this much of it is white instead.")] public float rimWhite = .25f;
            [Tooltip("Right at the outline the fold reaches this far towards the far side (the rim shows the colours opposite it).")] public float edgeReach = -.2f;
            [Tooltip("How thin that edge band is.")] public float edgePower = 60f;
            [Tooltip("Apple's Clear / Tinted switch as a slider: 0 = .clear, 1 = .regular (whiter, softer, more saturated, a highlight on capsules).")] public float matte = 0f;
            [Tooltip("The soft highlight a .regular capsule catches over its left cap (scaled by matte).")] public float highlight = .13f;
        }

        public static Look Settings = new();

        public const string ButtonClass = "liquid-glass";

        /// Makes a UI Toolkit element a glass button: the element's own drawing is hidden (it still
        /// takes the taps), the glass is drawn in its place and its content (text, icons, children)
        /// on top. `cornerRadius` in the panel's units, 0 = pill or circle.
        public static void Toolkit(VisualElement element, float cornerRadius = 0f)
        {
            if (element == null || LiquidGlassPass.Has(element)) return;
            for (var p = element.parent; p != null; p = p.parent) if (p.ClassListContains(ButtonClass)) return;
            element.AddToClassList(ButtonClass);
            Radii[element] = cornerRadius;
            if (element.panel == null) { element.RegisterCallback<AttachToPanelEvent>(OnAttached); return; }
            UIDocument owner = null;
            foreach (var doc in UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude))
                if (doc.rootVisualElement?.panel == element.panel) { owner = doc; break; }
            if (owner == null) { Debug.LogWarning($"[LiquidGlass] no UIDocument draws '{element.name}'"); return; }
            var copy = ToolkitContentCopy.Bind(owner, element);
            // Its own drawing goes (it still takes the taps): fade a parent to fade the glass.
            // Not an opacity filter: its shader is stripped from builds that use no filters.
            element.style.opacity = 0f;
            LiquidGlassPass.Add(new LiquidGlassPass.ToolkitItem { Element = element, Radius = cornerRadius, Copy = copy });
            element.RegisterCallback<DetachFromPanelEvent>(OnDetached);
        }

        /// The element stops being glass and draws itself again.
        public static void Remove(VisualElement element)
        {
            if (element == null) return;
            LiquidGlassPass.Remove(element);
            Radii.Remove(element);
            element.RemoveFromClassList(ButtonClass);
            element.style.opacity = StyleKeyword.Null;
            element.UnregisterCallback<AttachToPanelEvent>(OnAttached);
            element.UnregisterCallback<DetachFromPanelEvent>(OnDetached);
        }

        static readonly Dictionary<VisualElement, float> Radii = new();

        static void OnAttached(AttachToPanelEvent e)
        {
            if (e.target is not VisualElement element) return;
            element.UnregisterCallback<AttachToPanelEvent>(OnAttached);
            element.RemoveFromClassList(ButtonClass);
            Toolkit(element, Radii.TryGetValue(element, out var r) ? r : 0f);
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
            m.SetFloat("_Magnify", Mathf.Max(.1f, s.magnify));
            m.SetFloat("_RimReach", s.rimReach);
            m.SetFloat("_RimPower", Mathf.Max(1f, s.rimPower));
            m.SetFloat("_LensStrength", s.lensStrength);
            m.SetFloat("_Dispersion", s.dispersion);
            m.SetFloat("_Frost", Mathf.Clamp01(s.frost));
            m.SetFloat("_Specular", Mathf.Max(0f, s.specular));
            // matte 1 is Apple's .regular, measured against .clear on the same grid: 47% whiter,
            // blurred 2 points more, 1.4x as saturated, a highlight on capsules.
            m.SetFloat("_Tint", 1f - (1f - Mathf.Clamp01(s.tint)) * (1f - .47f * matte));
            m.SetFloat("_Brightness", Mathf.Max(0f, s.brightness));
            m.SetFloat("_FrostRadius", Mathf.Max(0f, s.frostRadius) + 2f * matte);
            m.SetFloat("_SourceBlur", Mathf.Max(0f, s.sourceBlur));
            m.SetFloat("_Highlight", Mathf.Max(0f, s.highlight) * matte);
            m.SetFloat("_Saturation", Mathf.Max(0f, s.saturation) + .4f * matte);
            m.SetFloat("_Contrast", Mathf.Max(0f, s.contrast));
            m.SetFloat("_RimWhite", Mathf.Clamp01(s.rimWhite));
            m.SetFloat("_EdgeReach", s.edgeReach);
            m.SetFloat("_EdgePower", Mathf.Max(1f, s.edgePower));
            m.SetVector("_LightDir", new Vector4(Mathf.Cos(angle), Mathf.Sin(angle)));
        }
    }
}
