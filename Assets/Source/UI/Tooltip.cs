using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class Tooltip : MonoBehaviour
{
    private Coroutine _settle;

    private const float UpOffsetFromAnchor = 0.05f;
    private const float NoticeDistanceFromCamera = 0.5f;

    private const float TitleHeight = Style.Title + Style.SmallPadding;
    private const float ContentTop = Style.SmallPadding + TitleHeight + Style.SmallPadding;
    private const float ContentBottom = Style.SmallPadding;
    private const float Borders = Style.SmallBorder * 2f;

    private Canvas _canvas;
    private RectTransform _canvasRect;

    private GameObject _hintGroup;
    private TextMeshProUGUI _titleLabel;
    private TextMeshProUGUI _bodyLabel;

    private void Awake()
    {
        _canvas = GetComponentInChildren<Canvas>(true);
        _canvasRect = _canvas != null ? _canvas.transform as RectTransform : null;

        if (_canvas != null) _canvas.sortingOrder = Style.SortNotices;

        if (_canvasRect != null)
        {
            _canvasRect.sizeDelta = new Vector2(Style.TooltipWidth, _canvasRect.sizeDelta.y);
            _canvasRect.pivot = new Vector2(0.5f, 0f);
            _canvasRect.localPosition = Vector3.zero;
        }

        _hintGroup = FindChild("HintGroup");
        _titleLabel = FindComponent<TextMeshProUGUI>("HintTitle");
        _bodyLabel = FindComponent<TextMeshProUGUI>("HintBody");

        PanelUI.ApplyPanelBorders(transform);
        PanelUI.ApplyPanelSprites(transform);

        NormalizeTitleHeight(RectOf(_titleLabel));
        EnableWrap(_titleLabel);
        EnableWrap(_bodyLabel);

        ApplyFonts();

        // Hidden, not switched off, as the panels are: showing a notice then
        // rebuilds no text or layout.
        if (_canvas != null) _canvas.enabled = false;
    }

    private void ApplyFonts()
    {
        Style.ApplyBody(_bodyLabel);
        Style.ApplyTitle(_titleLabel);
    }

    // Whether a notice is up, and whether Prewarm is drawing the hidden canvas,
    // unseen, so its text and layout are built at startup.
    private bool _shown;
    private bool _warming;
    private CanvasGroup _warmGroup;

    public void PrewarmCanvas(bool on)
    {
        if (_canvas == null || on == _warming) return;
        _warming = on;
        if (_warmGroup == null) _warmGroup = UILayout.Ensure<CanvasGroup>(_canvas.gameObject);
        _warmGroup.alpha = on && !_shown ? 0f : 1f;
        ApplyShown();
    }

    private void ApplyShown()
    {
        if (_canvas != null) _canvas.enabled = _shown || _warming;
    }

    private static void EnableWrap(TextMeshProUGUI label)
    {
        if (label == null) return;
        label.enableWordWrapping = true;
        label.overflowMode = TextOverflowModes.Overflow;
    }

    public void ShowNotice(string title, string body)
    {
        StopSettle();

        if (_hintGroup != null) _hintGroup.SetActive(true);

        if (_titleLabel != null) _titleLabel.text = title;
        if (_bodyLabel != null)
        {
            _bodyLabel.text = body;
            RectTransform bodyRT = _bodyLabel.rectTransform;
            bodyRT.anchoredPosition = new Vector2(bodyRT.anchoredPosition.x, -ContentTop);
        }

        Present(NoticeAnchor());
        SetPanelHeight(HeightFor(body));

        if (!isActiveAndEnabled) return;
        _settle = StartCoroutine(SettleHeight(_hintGroup, () => HeightFor(body)));
    }

    // Re-measures the panel over the next few frames, while text and layout
    // settle, for as long as 'group' is the one showing.
    private IEnumerator SettleHeight(GameObject group, Func<float> height)
    {
        yield return UILayout.Converge(
            () => _shown && group != null && group.activeInHierarchy,
            () => SetPanelHeight(height()),
            () => _canvasRect != null ? _canvasRect.sizeDelta.y : -1f);

        _settle = null;
    }

    private void StopSettle()
    {
        if (_settle != null) StopCoroutine(_settle);
        _settle = null;
    }

    private void OnDisable() => _settle = null;

    private Vector3 NoticeAnchor()
    {
        Transform cam = CameraRig.MainTransform;
        if (cam == null) return transform.position;

        return cam.position + CameraRig.FlatForward * NoticeDistanceFromCamera;
    }

    public void DismissNotice()
    {
        _shown = false;
        ApplyShown();
    }

    private float HeightFor(string body)
    {
        if (_bodyLabel == null) return Borders + ContentTop + ContentBottom;

        if (_canvasRect != null) LayoutRebuilder.ForceRebuildLayoutImmediate(_canvasRect);
        _bodyLabel.ForceMeshUpdate();
        float width = _bodyLabel.rectTransform.rect.width;
        return Borders + ContentTop + _bodyLabel.GetPreferredValues(body, width, 0f).y + ContentBottom;
    }

    private void SetPanelHeight(float height)
    {
        if (_canvasRect != null)
            _canvasRect.sizeDelta = new Vector2(_canvasRect.sizeDelta.x, height);
    }

    public bool IsVisible => _canvas != null && _shown;


    private static void NormalizeTitleHeight(RectTransform rt)
    {
        if (rt != null) rt.sizeDelta = new Vector2(rt.sizeDelta.x, TitleHeight);
    }

    public void UpdatePosition(Vector3 anchorPoint)
    {
        if (CameraRig.MainTransform == null) return;

        transform.position = anchorPoint + Vector3.up * UpOffsetFromAnchor;
        FaceViewer();
    }

    private void FaceViewer()
    {
        if (CameraRig.TryFaceViewer(transform.position, out Quaternion rotation))
            transform.rotation = rotation;
    }

    private void LateUpdate()
    {
        if (IsVisible) FaceViewer();
    }

    private void Present(Vector3 worldHitPoint)
    {
        UpdatePosition(worldHitPoint);
        _shown = true;
        if (_warmGroup != null) _warmGroup.alpha = 1f;
        ApplyShown();
    }

    private T FindComponent<T>(string name) where T : Component
    {
        Transform t = FindTransform.FindDeep(transform, name);
        return t != null ? t.GetComponent<T>() : null;
    }

    private GameObject FindChild(string name)
    {
        Transform t = FindTransform.FindDeep(transform, name);
        return t != null ? t.gameObject : null;
    }

    private static RectTransform RectOf(Component c) => c != null ? c.transform as RectTransform : null;
}
