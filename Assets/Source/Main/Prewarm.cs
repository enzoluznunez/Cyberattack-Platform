using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class Prewarm : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        var go = new GameObject("Prewarm");
        DontDestroyOnLoad(go);
        go.AddComponent<Prewarm>();
    }

    private IEnumerator Start()
    {
        yield return null;

        GameObject rig = BuildWarmRig();

        ToolPanelUI tool = Scene.ToolPanel;
        Tooltip tooltip = FindAnyObjectByType<Tooltip>(FindObjectsInactive.Include);

        if (tool != null) tool.PrewarmCanvas(true);
        if (tooltip != null) tooltip.PrewarmCanvas(true);
        // The click a poke plays, its clip made now rather than on the first poke.
        UIPressFeedback.Prepare();

        // Two frames: one to lay the nested layout groups out, one to draw
        // what that settled on.
        yield return null;
        yield return null;

        if (tool != null) tool.PrewarmCanvas(false);
        if (tooltip != null) tooltip.PrewarmCanvas(false);
        if (rig != null) Destroy(rig);

        Destroy(gameObject);
    }

    private static GameObject BuildWarmRig()
    {
        Transform cam = CameraRig.MainTransform;
        if (cam == null)
        {
            Debug.LogWarning("[Prewarm] No main camera yet; shader warm draws were skipped.");
            return null;
        }

        var root = new GameObject("PrewarmRig");
        root.transform.SetParent(cam, false);
        root.transform.localPosition = new Vector3(0f, 0f, 1f);

        var canvasGo = new GameObject("WarmCanvas", typeof(Canvas));
        canvasGo.transform.SetParent(root.transform, false);
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        var rect = (RectTransform)canvasGo.transform;
        rect.sizeDelta = new Vector2(2f, 2f);
        rect.localScale = Vector3.one * 0.001f;

        AddImage(canvasGo.transform, null);
        AddImage(canvasGo.transform, PanelUI.FrontMaterial);
        AddImage(canvasGo.transform, PanelUI.BackMaterial);
        AddLabel(canvasGo.transform, Style.BodyFont);
        AddLabel(canvasGo.transform, Style.TitleFont);

        // The bars, the maps and the sheet's titles are warmed by drawing
        // the real ones once the loading phase has built them (GraphicsWarm):
        // a quad in their materials warmed nothing they use, its mesh layout
        // not being theirs. These world titles stay for the glyphs below.
        foreach (TMP_FontAsset font in new[] { Style.BodyFont, Style.TitleFont })
        {
            if (font != null) font.TryAddCharacters(CommonGlyphs, out _);
            var label = new GameObject("WarmWorldLabel").AddComponent<TextMeshPro>();
            label.transform.SetParent(root.transform, false);
            label.transform.localScale = Vector3.one * 0.001f;
            label.text = "Warm";
            if (font != null) label.font = font;
        }

        return root;
    }

    // Every glyph a title is likely to use, added to the fonts at startup so
    // no sheet or panel adds them mid-session: printable ASCII, Latin-1's
    // letters and signs, and typographic punctuation.
    private static readonly string CommonGlyphs = BuildCommonGlyphs();

    private static string BuildCommonGlyphs()
    {
        var sb = new System.Text.StringBuilder();
        for (char c = ' '; c <= '~'; c++) sb.Append(c);
        for (char c = '\u00A1'; c <= '\u00FF'; c++) sb.Append(c);
        sb.Append("\u2018\u2019\u201C\u201D\u2013\u2014\u2026\u2022\u20AC\u2122");
        return sb.ToString();
    }

    private static void AddImage(Transform parent, Material material)
    {
        var go = new GameObject("WarmImage", typeof(Image));
        go.transform.SetParent(parent, false);
        ((RectTransform)go.transform).sizeDelta = new Vector2(2f, 2f);
        if (material != null) go.GetComponent<Image>().material = material;
    }

    private static void AddLabel(Transform parent, TMP_FontAsset font)
    {
        var go = new GameObject("WarmLabel", typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        ((RectTransform)go.transform).sizeDelta = new Vector2(2f, 2f);
        var label = go.GetComponent<TextMeshProUGUI>();
        label.text = "Warm";
        label.fontSize = 1f;
        if (font != null) label.font = font;
    }
}
