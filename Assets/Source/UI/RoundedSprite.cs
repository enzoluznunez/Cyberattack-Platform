using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public static class RoundedSprite
{
    private const int TexelsPerUnit = 4;
    private const float ReferencePixelsPerUnit = 100f;

    private static readonly Dictionary<int, Sprite> _sprites = new Dictionary<int, Sprite>();

    public static Sprite Get(float radiusUnits)
    {
        int r = Mathf.Max(2, Mathf.RoundToInt(radiusUnits * TexelsPerUnit));
        if (_sprites.TryGetValue(r, out Sprite cached) && cached != null) return cached;

        int size = r * 2 + 4;

        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, 1, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        Color32[] pixels = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            float py = y + 0.5f;
            float dy = py < r ? r - py : (py > size - r ? py - (size - r) : -1f);

            for (int x = 0; x < size; x++)
            {
                float px = x + 0.5f;
                float dx = px < r ? r - px : (px > size - r ? px - (size - r) : -1f);

                byte a = 255;
                if (dx > 0f && dy > 0f)
                    a = Coverage(Mathf.Sqrt(dx * dx + dy * dy), r + 0.5f);
                pixels[y * size + x] = new Color32(255, 255, 255, a);
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply(false, true);

        Vector4 border = new Vector4(r, r, r, r);
        Sprite sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
            ReferencePixelsPerUnit * TexelsPerUnit, 0, SpriteMeshType.FullRect, border);

        _sprites[r] = sprite;
        return sprite;
    }

    public static void Apply(Image image, float radiusUnits)
    {
        if (image == null) return;

        image.sprite = Get(radiusUnits);
        image.type = Image.Type.Sliced;
        image.fillCenter = true;
        image.pixelsPerUnitMultiplier = 1f;
        PanelUI.ApplyFront(image);
    }

    private static byte Coverage(float dist, float radius)
    {
        float edge = dist - radius;
        if (edge <= -0.5f) return 255;
        if (edge >= 0.5f) return 0;
        return (byte)Mathf.RoundToInt(255f * (0.5f - edge));
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        foreach (KeyValuePair<int, Sprite> kv in _sprites)
        {
            if (kv.Value == null) continue;
            Texture2D stale = kv.Value.texture;
            Object.Destroy(kv.Value);
            if (stale != null) Object.Destroy(stale);
        }
        _sprites.Clear();
    }
}
