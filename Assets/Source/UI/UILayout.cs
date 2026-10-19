using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using Oculus.Interaction;

public static class UILayout
{
    public const int SettlePasses = 4;

    public static void Clear(Transform root)
    {
        if (root == null) return;

        for (int i = root.childCount - 1; i >= 0; i--)
        {
            Transform child = root.GetChild(i);
            child.SetParent(null, false);
            UnityEngine.Object.Destroy(child.gameObject);
        }
    }

    // Hides or shows a canvas without switching anything off: it stops or
    // starts drawing, and its poke surfaces stop or start taking fingertips.
    // 'drawn' keeps it drawing while hidden, for a panel being warmed up.
    public static void ShowCanvas(Canvas canvas, PokeInteractable[] pokes, bool on, bool drawn = false)
    {
        if (!canvas.gameObject.activeSelf) canvas.gameObject.SetActive(true);
        canvas.enabled = on || drawn;
        for (int i = 0; i < pokes.Length; i++)
            if (pokes[i] != null) pokes[i].enabled = on;
    }

    // Hides or shows one part of a panel (a pane, a card) without switching it
    // off, so showing it again rebuilds no text or layout: it stops drawing,
    // stops taking pokes and leaves its parent's layout.
    public static void ShowPart(GameObject part, bool on)
    {
        if (part == null) return;
        if (!part.activeSelf) part.SetActive(true);
        CanvasGroup group = Ensure<CanvasGroup>(part);
        group.alpha = on ? 1f : 0f;
        group.interactable = on;
        group.blocksRaycasts = on;
        Ensure<LayoutElement>(part).ignoreLayout = !on;
    }

    // Whether a part is up: active, and not hidden by ShowPart.
    public static bool PartShown(GameObject part) =>
        part != null && part.activeInHierarchy &&
        !(part.TryGetComponent(out CanvasGroup group) && group.alpha <= 0f && !group.blocksRaycasts);

    // Fills the parent, held 'inset' in from each edge.
    public static void Stretch(RectTransform rt, float inset = 0f)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }

    public static T Ensure<T>(GameObject go) where T : Component
    {
        T c = go.GetComponent<T>();
        return c != null ? c : go.AddComponent<T>();
    }

    public static RectTransform EnsureChild(Transform parent, string name)
    {
        Transform existing = parent.Find(name);
        if (existing != null) return existing as RectTransform ?? existing.gameObject.AddComponent<RectTransform>();
        GameObject child = new GameObject(name);
        child.transform.SetParent(parent, false);
        return child.AddComponent<RectTransform>();
    }

    public static LayoutElement FixedHeight(GameObject go, float height)
    {
        LayoutElement le = Ensure<LayoutElement>(go);
        le.minHeight = height;
        le.preferredHeight = height;
        le.flexibleHeight = 0f;
        return le;
    }

    public static LayoutElement FixedSize(GameObject go, Vector2 size)
    {
        LayoutElement le = FixedHeight(go, size.y);
        le.minWidth = size.x;
        le.preferredWidth = size.x;
        le.flexibleWidth = 0f;
        return le;
    }

    public static IEnumerator Converge(Func<bool> alive, Action apply, Func<float> measure)
    {
        float previous = measure();

        for (int pass = 0; pass < SettlePasses; pass++)
        {
            yield return null;
            if (!alive()) break;

            apply();

            float current = measure();
            if (Mathf.Approximately(current, previous)) break;
            previous = current;
        }
    }
}
