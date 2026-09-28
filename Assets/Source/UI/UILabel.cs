using UnityEngine;
using TMPro;

public static class UILabel
{
    public static TextMeshProUGUI Make(Transform parent, string name,
        Color color, TextAlignmentOptions alignment, TextOverflowModes overflow,
        bool anchorTopLeft = false, bool bold = false, bool wrap = false)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform));
        RectTransform rt = obj.GetComponent<RectTransform>();
        rt.SetParent(parent, false);

        if (anchorTopLeft)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
        }

        TextMeshProUGUI label = obj.AddComponent<TextMeshProUGUI>();
        if (bold) Style.ApplyTitle(label);
        else Style.ApplyBody(label);
        label.color = color;
        label.alignment = alignment;
        label.overflowMode = overflow;
        label.enableWordWrapping = wrap;
        label.raycastTarget = false;
        return label;
    }
}
