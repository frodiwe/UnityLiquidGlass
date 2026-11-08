using System.Collections.Generic;
using UnityEngine;

namespace UnityLiquidGlass
{
    /// A sprite's outline as a signed distance field, for glass of any shape. Built once per sprite
    /// from its alpha (the sprite needs no Read/Write flag) and cached. Each texel holds the signed
    /// distance to the outline in texels (negative inside) and the outward normal.
    static class ShapeField
    {
        internal sealed class Field
        {
            public Texture2D Texture;
            public Vector4 Uv;       // shape uv (0..1 over the sprite's rect) → texture uv: scale xy, offset zw
            public Vector4 Texels;   // the sprite's rect in texels (w, h), the deepest inside distance
        }

        const int Size = 192, Pad = 8;
        static readonly Dictionary<Sprite, Field> Cache = new();

        internal static Field Of(Sprite sprite)
        {
            if (sprite == null) return null;
            if (Cache.TryGetValue(sprite, out var f) && f.Texture != null) return f;
            return Cache[sprite] = Build(sprite);
        }

        static Field Build(Sprite sprite)
        {
            var rect = sprite.rect.size;
            float scale = Size / Mathf.Max(rect.x, rect.y);
            int iw = Mathf.Max(1, Mathf.RoundToInt(rect.x * scale)), ih = Mathf.Max(1, Mathf.RoundToInt(rect.y * scale));
            int w = iw + 2 * Pad, h = ih + 2 * Pad;

            // The sprite's alpha, drawn as Image draws it: its trimmed padding left transparent.
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var was = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, w, h, 0);   // DrawTexture's rects run top-down
            var pad = UnityEngine.Sprites.DataUtility.GetPadding(sprite);   // left, bottom, right, top
            var o = UnityEngine.Sprites.DataUtility.GetOuterUV(sprite);
            var dest = Rect.MinMaxRect(Pad + pad.x * scale, Pad + pad.w * scale, Pad + (rect.x - pad.z) * scale, Pad + (rect.y - pad.y) * scale);
            Graphics.DrawTexture(dest, sprite.texture, Rect.MinMaxRect(o.x, o.y, o.z, o.w), 0, 0, 0, 0);
            GL.PopMatrix();
            var read = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            read.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            RenderTexture.active = was;
            RenderTexture.ReleaseTemporary(rt);
            var px = read.GetPixels32();
            Object.Destroy(read);

            var inside = new bool[w * h];
            // Any visible pixel is the shape: a glass plate's art is often a faint fill (hint_plate: 7% alpha).
            for (int i = 0; i < px.Length; i++) inside[i] = px[i].a >= 8;
            var din = Transform(inside, w, h, true);    // inside texels: squared distance to the nearest outside one
            var dout = Transform(inside, w, h, false);  // outside texels: to the nearest inside one
            // Texel steps in the distance, and normals from them, show as ripples and streaks on the
            // rim (the edge fold magnifies them): the field is smoothed a little.
            var d = new float[w * h];
            float deepest = 1f;
            for (int i = 0; i < d.Length; i++)
                d[i] = inside[i] ? .5f - Mathf.Sqrt(din[i]) : Mathf.Sqrt(dout[i]) - .5f;
            d = Blur(Blur(d, w, h, 2), w, h, 2);
            foreach (var v in d) deepest = Mathf.Max(deepest, -v);
            var data = new Color[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float gx = d[y * w + Mathf.Min(x + 2, w - 1)] - d[y * w + Mathf.Max(x - 2, 0)];
                float gy = d[Mathf.Min(y + 2, h - 1) * w + x] - d[Mathf.Max(y - 2, 0) * w + x];
                var n = new Vector2(gx, gy);
                n = n.sqrMagnitude > 1e-6f ? n.normalized : Vector2.up;
                data[y * w + x] = new Color(d[y * w + x], n.x, n.y, 0);
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBAHalf, false, true)
            { name = $"Glass shape {sprite.name}", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            tex.SetPixels(data);
            tex.Apply(false, true);
            return new Field
            {
                Texture = tex,
                Uv = new Vector4(iw / (float)w, ih / (float)h, Pad / (float)w, Pad / (float)h),
                Texels = new Vector4(iw, ih, deepest, 0),
            };
        }

        /// A box blur of radius r, separable.
        static float[] Blur(float[] src, int w, int h, int r)
        {
            var tmp = new float[w * h]; var dst = new float[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float sum = 0; for (int k = -r; k <= r; k++) sum += src[y * w + Mathf.Clamp(x + k, 0, w - 1)];
                tmp[y * w + x] = sum / (2 * r + 1);
            }
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float sum = 0; for (int k = -r; k <= r; k++) sum += tmp[Mathf.Clamp(y + k, 0, h - 1) * w + x];
                dst[y * w + x] = sum / (2 * r + 1);
            }
            return dst;
        }

        /// Squared Euclidean distance from every texel to the nearest texel where inside != of
        /// (Felzenszwalb and Huttenlocher): texels where inside == of are measured, others are 0.
        static float[] Transform(bool[] inside, int w, int h, bool of)
        {
            const float Inf = 1e20f;
            var g = new float[w * h];
            for (int i = 0; i < g.Length; i++) g[i] = inside[i] == of ? Inf : 0f;
            int n = Mathf.Max(w, h);
            var f = new float[n]; var r = new float[n]; var v = new int[n]; var z = new float[n + 1];
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++) f[y] = g[y * w + x];
                Line(f, h, r, v, z);
                for (int y = 0; y < h; y++) g[y * w + x] = r[y];
            }
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++) f[x] = g[y * w + x];
                Line(f, w, r, v, z);
                for (int x = 0; x < w; x++) g[y * w + x] = r[x];
            }
            return g;
        }

        static void Line(float[] f, int n, float[] d, int[] v, float[] z)
        {
            int k = 0; v[0] = 0; z[0] = float.NegativeInfinity; z[1] = float.PositiveInfinity;
            for (int q = 1; q < n; q++)
            {
                float s = (f[q] + q * q - (f[v[k]] + v[k] * v[k])) / (2f * q - 2f * v[k]);
                while (s <= z[k])
                {
                    k--;
                    s = (f[q] + q * q - (f[v[k]] + v[k] * v[k])) / (2f * q - 2f * v[k]);
                }
                k++; v[k] = q; z[k] = s; z[k + 1] = float.PositiveInfinity;
            }
            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                d[q] = (q - v[k]) * (q - v[k]) + f[v[k]];
            }
        }
    }
}
