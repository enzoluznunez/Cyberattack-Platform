using System;
using System.Collections;
using TMPro;
using UnityEngine;

public static class Style
{
    private const string BodyFontPath = "Fonts/Inter-Medium SDF";
    private const string TitleFontPath = "Fonts/Inter-Bold SDF";

    private static TMP_FontAsset _bodyFont;
    private static TMP_FontAsset _titleFont;

    public static TMP_FontAsset BodyFont => Load(ref _bodyFont, BodyFontPath);

    public static TMP_FontAsset TitleFont => Load(ref _titleFont, TitleFontPath);

    public static void ApplyBody(TMP_Text label)
    {
        if (label == null) return;
        if (BodyFont != null) label.font = BodyFont;
        label.fontSize = Body;
    }

    public static void ApplyTitle(TMP_Text label)
    {
        if (label == null) return;
        if (TitleFont != null) label.font = TitleFont;
        label.fontSize = Title;
    }

    private static TMP_FontAsset Load(ref TMP_FontAsset cached, string path)
    {
        if (cached != null) return cached;

        cached = Resources.Load<TMP_FontAsset>(path);
        if (cached == null)
            Debug.LogError($"[Style] No font at Resources/{path}; text keeps whatever font TMP defaults to.");
        return cached;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _bodyFont = null;
        _titleFont = null;
    }

    public static readonly Color White = Hex(255, 255, 255);
    public static readonly Color Black = Hex(000, 000, 000);

    public const float SurfaceTint = 0.05f;

    public const float EngageScale = 1.03f;
    public const float EngageSwell = EngageScale - 1f;
    public const float PreviewSwell = 0.10f;

    public const float SmallBorder = 2f;

    public const float Body = 10f;
    public const float Title = 12f;

    public const float WorldScale = 0.001f;
    public const float WorldTextScale = WorldScale * 10f;

    // How far along a timed motion is, for its share of time 't': fast at
    // first and slowing into place. Every timed motion uses it, the sheet's,
    // its lines' and strips' and the map's alike, so all of them move the
    // same way.
    public static float EaseOut(float t)
    {
        t = Mathf.Clamp01(t);
        return 1f - (1f - t) * (1f - t) * (1f - t);
    }

    // A timed motion over 'seconds': from the next frame on, hands 'apply'
    // how far along it is (EaseOut), ending at 1.
    public static IEnumerator Tween(float seconds, Action<float> apply)
    {
        float t = 0f;
        while (t < seconds)
        {
            yield return null;
            t += Time.deltaTime;
            apply(EaseOut(t / seconds));
        }
    }

    // How much of the way to close toward a target this frame, closing at
    // 'rate' per second however long the frame was.
    public static float Damp(float rate, float dt) => 1f - Mathf.Exp(-rate * dt);

    public const float SmallPadding = 6f;
    public const float MediumPadding = 12f;
    public const float ButtonTextPad = 10f;
    public const float PanelInset = MediumPadding;

    public const float HeaderHeight = 30f;

    public const float OutRadius = 8f;
    public const float ChipRadius = 4f;
    public const float PanelRadius = 12f;
    public const float PanelInnerRadius = PanelRadius - SmallBorder;

    public static float RadiusFor(float height) => height <= Subbutton.y ? ChipRadius : OutRadius;

    public static float InnerRadius(float radius) => radius - SmallBorder;

    // A tile is a button the size of a card, and a fixed corner would read as a
    // square beside the buttons it shares a panel with. Its radius is a share of
    // its side instead, held between a button's corner and the panel's own so
    // the whole surface stays one family. Whole units keep the rounded sprites
    // to a handful as a tile is resized.
    public const float TileRadiusRatio = 0.12f;

    public static float TileRadius(float side) =>
        Mathf.Round(Mathf.Clamp(side * TileRadiusRatio, OutRadius, PanelRadius + SmallPadding));

    public static readonly Vector2 Subbutton = new Vector2(40f, 20f);
    public static readonly Vector2 Button = new Vector2(50f, 25f);
    public const float TitleBarButtonWidth = 80f;

    public const int SortPanels = 0;
    public const int SortHandUI = 0;
    public const int SortNotices = 10;

    public static readonly Vector2 Subpanel = new Vector2(400f, 280f);
    public static readonly Vector2 Panel = new Vector2(400f, 400f);
    public const float TooltipWidth = 360f;

    public static Color Alpha(Color c, float a)
    {
        c.a = a;
        return c;
    }

    internal static Color Hex(byte r, byte g, byte b) => new Color32(r, g, b, 255);
}
