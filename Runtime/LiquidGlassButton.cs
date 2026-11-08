using UnityEngine;
using UnityEngine.UI;

namespace UnityLiquidGlass
{
    /// A uGUI glass button. Put it on a UI object with a Graphic (an Image with no sprite is fine):
    /// that graphic's rect is the glass's shape and stays the tap target. Its children (images,
    /// text, TextMeshPro) are drawn on top of the glass. Screen Space - Overlay canvases only.
    [RequireComponent(typeof(Graphic))]
    [AddComponentMenu("UI/Liquid Glass Button")]
    public sealed class LiquidGlassButton : MonoBehaviour
    {
        [Tooltip("Corner radius in canvas units; 0 = pill or circle.")]
        public float cornerRadius;
        [Tooltip("Transparent margin around the glass inside this rect, in canvas units.")]
        public float inset;
        [Tooltip("A custom shape: the glass takes this sprite's outline (its alpha), stretched over the rect. Empty = a rounded box.")]
        public Sprite shape;

        internal Graphic Shape;

        void OnEnable()
        {
            Shape = GetComponent<Graphic>();
            var canvas = Shape.canvas;
            if (canvas != null && canvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
                Debug.LogWarning($"[LiquidGlass] '{name}': only Screen Space - Overlay canvases are supported", this);
            LiquidGlassPass.Add(this);
        }

        void OnDisable() => LiquidGlassPass.Remove(this);
    }

    /// Makes a graphic draw LiquidGlassPass's marker (or nothing, for content) in the UI pass,
    /// whatever its owner assigns to it later.
    [DisallowMultipleComponent, AddComponentMenu("")]
    sealed class LiquidGlassMarker : MonoBehaviour, IMaterialModifier
    {
        // uGUI asks every modifier, enabled or not: one on its way out must already be off.
        public Material GetModifiedMaterial(Material baseMaterial) =>
            isActiveAndEnabled ? LiquidGlassPass.MarkerMaterial ?? baseMaterial : baseMaterial;
        void OnEnable() { if (TryGetComponent<Graphic>(out var g)) g.SetMaterialDirty(); }
        void OnDisable() { if (TryGetComponent<Graphic>(out var g)) g.SetMaterialDirty(); }
    }

    /// Keeps a copy of a content graphic's final mesh (every mesh effect applied), so the pass can
    /// draw any uGUI graphic on the glass: images of any type, raw images, legacy text, outlines.
    [DisallowMultipleComponent, AddComponentMenu("")]
    sealed class LiquidGlassContent : BaseMeshEffect
    {
        internal Mesh Mesh;

        protected override void OnEnable()
        {
            // Last in line, so it copies what every other mesh effect made.
            base.OnEnable();
            if (graphic != null) graphic.SetVerticesDirty();
        }

        public override void ModifyMesh(VertexHelper vh)
        {
            if (Mesh == null) Mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            vh.FillMesh(Mesh);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            if (Mesh != null) Destroy(Mesh);
        }
    }
}
