using UnityEngine;
using UnityEngine.UI;
using TMPro;

public static class UIButton
{
    public class Handle
    {
        public GameObject Root;
        public Button Button;
        public Image Frame;
        public Image Inner;
        public Image Back;
        public TextMeshProUGUI Text;
        public PointerHighlight Highlight;
        public float Radius;

        public Color RestFill;
        public Color SelectFill;
        public Color RestText;
        public Color SelectText;
    }

    public static Handle Create(Transform parent, string name, string label,
        float width = 0f, bool flexibleWidth = false)
    {
        GameObject root = new GameObject(name);
        root.transform.SetParent(parent, false);
        root.AddComponent<RectTransform>();

        LayoutElement le = root.AddComponent<LayoutElement>();
        le.minHeight = Style.Button.y;
        le.preferredHeight = le.minHeight;
        if (width > 0f) le.preferredWidth = width;
        le.flexibleWidth = flexibleWidth ? 1f : 0f;

        Handle h = new Handle { Root = root };
        Build(h, label, Style.RadiusFor(le.minHeight));
        return h;
    }

    public static Handle Adopt(GameObject root)
    {
        Handle h = new Handle { Root = root };
        Build(h, null, Style.OutRadius);
        return h;
    }

    public static Image AddBack(Handle h)
    {
        if (h == null || h.Root == null) return null;
        if (h.Back != null) return h.Back;

        RectTransform rt = UILayout.EnsureChild(h.Root.transform, "ButtonBack");
        UILayout.Stretch(rt);

        Image back = UILayout.Ensure<Image>(rt.gameObject);
        RoundedSprite.Apply(back, h.Radius, back: true);
        back.color = Style.Black;
        back.raycastTarget = false;

        rt.SetAsLastSibling();

        h.Back = back;
        return back;
    }

    // Re-rounds a button that has been resized since it was built: the frame, the
    // inner fill and the back all carry the same corner, so they are re-cut
    // together or the borders stop tracking one another.
    public static void SetRadius(Handle h, float radius)
    {
        if (h == null || h.Root == null || Mathf.Approximately(h.Radius, radius)) return;
        h.Radius = radius;

        if (h.Frame != null) RoundedSprite.Apply(h.Frame, radius);
        if (h.Inner != null) RoundedSprite.Apply(h.Inner, Style.InnerRadius(radius));

        if (h.Back != null) RoundedSprite.Apply(h.Back, radius, back: true);
    }

    public static void SetSelected(Handle h, bool selected)
    {
        if (h == null) return;

        Color fill = selected ? h.SelectFill : h.RestFill;
        Color press = selected ? h.RestFill : h.SelectFill;
        Color textColor = selected ? h.SelectText : h.RestText;
        Color pressText = selected ? h.RestText : h.SelectText;

        if (h.Highlight != null) h.Highlight.SetState(fill, press, textColor, pressText);
        else if (h.Inner != null) h.Inner.color = fill;

        if (h.Text != null) h.Text.color = textColor;
    }

    private static void SetStateTarget(Handle h, Graphic target,
        Color rest, Color select, Color restText, Color selectText)
    {
        h.RestFill = rest;
        h.SelectFill = select;
        h.RestText = restText;
        h.SelectText = selectText;
        if (h.Highlight == null) return;

        h.Highlight.target = target;
        if (h.Text != null) h.Highlight.SetTextTarget(h.Text);
        h.Highlight.SetState(rest, select, restText, selectText);
        target.color = rest;
    }

    private static void Build(Handle h, string label, float radius)
    {
        GameObject root = h.Root;
        h.Radius = radius;

        Image frame = UILayout.Ensure<Image>(root);
        RoundedSprite.Apply(frame, radius);
        frame.color = Style.Black;
        // A press lands a little outside the button too (PokeRule), as the
        // map's pins and sliders take one; negative padding reaches out.
        float slack = PokeRule.EdgeSlack * PokeRule.Millimetres;
        frame.raycastPadding = new Vector4(-slack, -slack, -slack, -slack);
        h.Frame = frame;

        Button button = UILayout.Ensure<Button>(root);
        button.transition = Selectable.Transition.None;
        button.targetGraphic = frame;
        // A button added under a part that is hidden (UILayout.ShowPart, so
        // not interactable) tints its frame to its disabled colour the moment
        // it is added, before the transition above is off; turning it off
        // leaves that tint, so it is cleared here.
        frame.canvasRenderer.SetColor(Color.white);
        h.Button = button;

        RectTransform innerRt = UILayout.EnsureChild(root.transform, "Inner");
        UILayout.Stretch(innerRt, Style.SmallBorder);
        Image inner = UILayout.Ensure<Image>(innerRt.gameObject);
        RoundedSprite.Apply(inner, Style.InnerRadius(radius));
        inner.raycastTarget = false;
        h.Inner = inner;

        RectTransform textRt = UILayout.EnsureChild(root.transform, "Text");
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = new Vector2(Style.ButtonTextPad, 0f);
        textRt.offsetMax = new Vector2(-Style.ButtonTextPad, 0f);
        TextMeshProUGUI text = UILayout.Ensure<TextMeshProUGUI>(textRt.gameObject);
        if (label != null) text.text = label;
        Style.ApplyBody(text);
        text.alignment = TextAlignmentOptions.Center;
        text.color = Style.Black;
        text.raycastTarget = false;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        h.Text = text;

        PointerHighlight hl = UILayout.Ensure<PointerHighlight>(root);
        hl.SetLiftTarget(root.transform);
        h.Highlight = hl;

        SetStateTarget(h, inner, Style.White, Style.Black, Style.Black, Style.White);

        if (root.transform.Find("ButtonBack") != null) AddBack(h);
    }
}
