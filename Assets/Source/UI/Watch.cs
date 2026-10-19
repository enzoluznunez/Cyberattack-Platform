using System;
using UnityEngine;
using UnityEngine.UI;
using Oculus.Interaction;
using Oculus.Interaction.Input;

public enum AssistantCause { User, Agent, Failure }

public class Watch : MonoBehaviour
{
    [SerializeField, Interface(typeof(IHand))]
    private UnityEngine.Object _hand;

    public ToolPanelUI toolPanelUI;
    public GeminiClient geminiClient;

    public float yOffsetFromHand = 0.03f;

    [Header("Pose smoothing")]
    [Tooltip("Lowest position smoothing frequency in Hz; lower removes more shimmer at rest.")]
    public float posMinCutoff = 1f;
    [Tooltip("How quickly position smoothing releases as the hand speeds up; raise if the watch trails fast motion.")]
    public float posBeta = 0.02f;
    [Tooltip("Lowest rotation smoothing frequency in Hz; lower removes more shimmer at rest.")]
    public float rotMinCutoff = 1f;
    [Tooltip("How quickly rotation smoothing releases as the hand speeds up.")]
    public float rotBeta = 0.05f;
    [Tooltip("Smoothing multiplier while hand tracking confidence is low; lower holds steadier.")]
    [Range(0.05f, 1f)] public float lowConfidenceHardening = 0.25f;

    private const float HandPlacement = 0.6f;
    private const HandJointId AlongJoint = HandJointId.HandMiddle1;
    private const HandJointId IndexJoint = HandJointId.HandIndex1;
    private const HandJointId PinkyJoint = HandJointId.HandPinky1;

    private const float TrackingGraceSeconds = 0.25f;
    private const float CalibrationRate = 2f;

    public event Action<bool, GeminiStatus> AssistantActiveChanged;

    private IHand _ihand;
    private UIButton.Handle _toolBtn;
    private UIButton.Handle _mapBtn;

    private Canvas _canvas;
    private Transform _canvasTransform;
    private bool _toolPanelOpen;
    private bool _mapPanelOpen;

    private bool _raisedActive;
    private GeminiStatus _raisedStatus;
    private bool _hasRaised;

    private Vector3 _posePosition;
    private Quaternion _poseRotation = Quaternion.identity;
    private bool _hasPose;
    private float _lostAt = -1f;

    private Vector3 _anchorOffset;
    private Quaternion _anchorRotation = Quaternion.identity;
    private bool _calibrated;

    private readonly OneEuroPoseFilter _poseFilter = new OneEuroPoseFilter();

    private void Awake()
    {
        _ihand = _hand as IHand;

        _canvas = GetComponentInChildren<Canvas>(true);
        if (_canvas != null)
        {
            _canvasTransform = _canvas.transform;
            _canvas.sortingOrder = Style.SortHandUI;
        }

        BindButtons();

        if (_canvas != null)
        {
            _pokes = _canvas.GetComponentsInChildren<PokeInteractable>(true);
            ShowCanvas(false);
        }
    }

    // Two buttons, one above the other, each opening and closing its own
    // panel: Map Panel, made from the scene's Tool Panel button, then Tool
    // Panel. Both are in the Undo All button's style, as wide and twice as
    // tall so a fingertip finds them easily, floating apart with nothing
    // behind them.
    private void BindButtons()
    {
        Transform tool = FindTransform.FindDeep(transform, "Tool Panel_Btn");
        if (tool != null && FindTransform.FindDeep(transform, "Map Panel_Btn") == null)
        {
            GameObject map = Instantiate(tool.gameObject, tool.parent, false);
            map.name = "Map Panel_Btn";
            map.transform.SetSiblingIndex(tool.GetSiblingIndex());
        }

        _mapBtn = BindPanelButton("Map Panel_Btn", "Map Panel", OnMapClicked);
        _toolBtn = BindPanelButton("Tool Panel_Btn", "Tool Panel", OnToolClicked);
        HideUnboundButtons();
        SizeToButtons();
    }

    // The watch shows exactly the buttons it binds. Anything else left in the
    // group — a button for a panel that no longer exists — is taken off the
    // layout rather than left sitting there doing nothing.
    private void HideUnboundButtons()
    {
        RectTransform group = FindTransform.FindDeep(transform, "Buttons") as RectTransform;
        if (group == null) return;

        GameObject tool = _toolBtn != null ? _toolBtn.Root : null;
        GameObject map = _mapBtn != null ? _mapBtn.Root : null;
        for (int i = 0; i < group.childCount; i++)
        {
            GameObject child = group.GetChild(i).gameObject;
            if (child != tool && child != map && child.activeSelf) child.SetActive(false);
        }
    }

    private void SizeToButtons()
    {
        RectTransform group = FindTransform.FindDeep(transform, "Buttons") as RectTransform;
        VerticalLayoutGroup vlg = group != null ? group.GetComponent<VerticalLayoutGroup>() : null;
        if (vlg == null) return;

        int margin = (int)Style.SmallPadding;
        vlg.spacing = ButtonGap;
        vlg.padding = new RectOffset(margin, margin, margin, margin);
        vlg.childAlignment = TextAnchor.MiddleCenter;

        int count = 0;
        for (int i = 0; i < group.childCount; i++)
        {
            GameObject child = group.GetChild(i).gameObject;
            if (!child.activeSelf || child.GetComponent<LayoutElement>() == null) continue;

            UILayout.FixedSize(child, ButtonSize);
            count++;
        }
        if (count == 0) return;

        Vector2 size = new Vector2(
            ButtonSize.x + vlg.padding.left + vlg.padding.right,
            count * ButtonSize.y + (count - 1) * vlg.spacing
                + vlg.padding.top + vlg.padding.bottom);

        RectTransform canvasRect = _canvas != null ? _canvas.transform as RectTransform : null;
        if (canvasRect != null) canvasRect.sizeDelta = size;

        group.pivot = new Vector2(0.5f, 0.5f);
        UILayout.Stretch(group);
    }

    // The Undo All button's width and twice its height, and the space between
    // the two, in the canvas's units.
    private static readonly Vector2 ButtonSize = new Vector2(Style.TitleBarButtonWidth, Style.Button.y * 2f);
    private const float ButtonGap = 10f;


    private UIButton.Handle BindPanelButton(string name, string label, UnityEngine.Events.UnityAction onClick)
    {
        Transform t = FindTransform.FindDeep(transform, name);
        if (t == null)
        {
            Debug.LogError($"[Watch] No button named '{name}' under the watch; that panel cannot be opened from the hand menu.");
            return null;
        }

        UIButton.Handle h = UIButton.Adopt(t.gameObject);
        if (h.Text != null)
        {
            h.Text.gameObject.SetActive(true);
            h.Text.text = label;
        }
        h.Button.onClick.AddListener(onClick);
        UIButton.SetSelected(h, false);
        return h;
    }

    private void OnToolClicked()
    {
        if (toolPanelUI != null) toolPanelUI.TogglePanel();
    }

    private void OnMapClicked()
    {
        if (MapCarousel.Instance != null) MapCarousel.Instance.Toggle();
    }


    public bool IsGeminiActive => geminiClient != null && geminiClient.Active;

    public GeminiStatus Status => Gemini.Status;

    public void SetGeminiActive(bool active, AssistantCause cause)
    {
        if (geminiClient == null) return;
        if (geminiClient.Active == active) return;
        if (!active && cause != AssistantCause.Agent) AgentTurn.UserTookControl();
        if (!active) Notices.HideNotice();
        geminiClient.SetActive(active);
        ReportActivation(active, cause);
        RaiseAssistantChanged();
    }

    private void ReportActivation(bool active, AssistantCause cause)
    {
        string what = active
            ? "the assistant is on and listening"
            : "the assistant is off";

        if (cause == AssistantCause.Failure) StateChannel.SetState("assistant", what);
        else StateChannel.RecordState("assistant", what);
    }

    private void RaiseAssistantChanged()
    {
        GeminiStatus status = Gemini.Status;
        bool active = IsGeminiActive;
        if (_hasRaised && _raisedActive == active && _raisedStatus == status) return;
        _hasRaised = true;
        _raisedActive = active;
        _raisedStatus = status;
        AssistantActiveChanged?.Invoke(active, status);
    }

    private void SyncPanelButtonStates()
    {
        if (toolPanelUI != null) SyncPanelButton(toolPanelUI.IsVisible, _toolBtn, ref _toolPanelOpen);
        MapCarousel map = MapCarousel.Instance;
        if (map != null) SyncPanelButton(map.IsOpen, _mapBtn, ref _mapPanelOpen);
    }

    // A button shows as selected while its panel is open.
    private static void SyncPanelButton(bool open, UIButton.Handle button, ref bool wasOpen)
    {
        if (open == wasOpen) return;

        wasOpen = open;
        UIButton.SetSelected(button, open);
    }

    private void OnEnable()
    {
        _poseFilter.Reset();
        _hasPose = false;
        _lostAt = -1f;

        if (geminiClient == null) return;
        geminiClient.ActiveChanged += OnGeminiActiveChanged;
        geminiClient.StatusChanged += OnGeminiStatusChanged;
        geminiClient.ContextWarning += OnContextWarning;
        geminiClient.ContextExhausted += OnContextExhausted;
    }

    private void OnDisable()
    {
        if (geminiClient == null) return;
        geminiClient.ActiveChanged -= OnGeminiActiveChanged;
        geminiClient.StatusChanged -= OnGeminiStatusChanged;
        geminiClient.ContextWarning -= OnContextWarning;
        geminiClient.ContextExhausted -= OnContextExhausted;
    }

    private void OnGeminiActiveChanged(bool active) => RaiseAssistantChanged();

    private void OnGeminiStatusChanged(GeminiStatus status)
    {
        if (status == GeminiStatus.Failed || status == GeminiStatus.MicDenied)
        {
            bool wasActive = IsGeminiActive;
            SetGeminiActive(false, AssistantCause.Failure);

            if (wasActive)
                Notices.Show(this, "Assistant Off",
                    status == GeminiStatus.MicDenied
                        ? "Microphone access is denied, so the assistant cannot listen."
                        : "The assistant could not stay connected and turned itself off.");
        }

        RaiseAssistantChanged();
    }

    private void OnContextWarning()
    {
        Notices.Show(this, "Assistant Nearly Full",
            "The assistant is close to the limit of what it can hold in mind. " +
            "It will reset itself shortly and start over from a blank slate.");
    }

    private void OnContextExhausted()
    {
        SetGeminiActive(false, AssistantCause.Failure);
        var toolPanel = Scene.ToolPanel;
        bool panelOpen = toolPanel != null && toolPanel.IsVisible;
        Notices.Show(this, "Assistant Full",
            panelOpen
                ? "The assistant ran out of room to hold the conversation and stopped. Select the assistant again to start over."
                : "The assistant ran out of room to hold the conversation and stopped. To start over, open the Tool Panel and select the assistant.");
    }

    private bool Engaged => IsEngaged(_toolBtn) || IsEngaged(_mapBtn);

    private static bool IsEngaged(UIButton.Handle h) => h != null && h.Highlight != null && h.Highlight.IsEngaged;

    private void LateUpdate()
    {
        SyncPanelButtonStates();

        if (_ihand == null || _canvasTransform == null) return;

        if (!TryReadHandPose(out Vector3 position, out Quaternion rotation))
        {
            HoldOrHide();
            return;
        }

        _lostAt = -1f;

        if (!_hasPose) _poseFilter.Reset();
        _poseFilter.posMinCutoff = posMinCutoff;
        _poseFilter.posBeta = posBeta;
        _poseFilter.rotMinCutoff = rotMinCutoff;
        _poseFilter.rotBeta = rotBeta;

        float hardening = _ihand.IsHighConfidence ? 1f : lowConfidenceHardening;
        Pose filtered = _poseFilter.Filter(new Pose(position, rotation), Time.unscaledDeltaTime, hardening);

        _posePosition = filtered.position;
        _poseRotation = filtered.rotation;
        _hasPose = true;

        _canvasTransform.SetPositionAndRotation(_posePosition, _poseRotation);
        SetVisible(true);
    }

    private void HoldOrHide()
    {
        if (!_hasPose)
        {
            SetVisible(false);
            return;
        }

        if (_lostAt < 0f) _lostAt = Time.unscaledTime;

        if (Time.unscaledTime - _lostAt < TrackingGraceSeconds || Engaged)
        {
            _canvasTransform.SetPositionAndRotation(_posePosition, _poseRotation);
            SetVisible(true);
            return;
        }

        _hasPose = false;
        SetVisible(false);
    }

    private bool TryReadHandPose(out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;

        if (!_ihand.IsTrackedDataValid) return false;
        if (!_ihand.GetJointPose(HandJointId.HandWristRoot, out Pose wrist)) return false;

        TryCalibrate(wrist);
        if (!_calibrated) return TryFingerPose(wrist, out position, out rotation);

        rotation = wrist.rotation * _anchorRotation;
        position = wrist.position + wrist.rotation * _anchorOffset;
        return true;
    }

    private void TryCalibrate(Pose wrist)
    {
        if (!_ihand.IsHighConfidence) return;
        if (!_ihand.GetFingerIsHighConfidence(HandFinger.Index) ||
            !_ihand.GetFingerIsHighConfidence(HandFinger.Middle) ||
            !_ihand.GetFingerIsHighConfidence(HandFinger.Pinky)) return;

        if (!TryFingerPose(wrist, out Vector3 position, out Quaternion rotation)) return;

        Quaternion inverse = Quaternion.Inverse(wrist.rotation);
        Vector3 offset = inverse * (position - wrist.position);
        Quaternion twist = inverse * rotation;

        if (!_calibrated)
        {
            _anchorOffset = offset;
            _anchorRotation = twist;
            _calibrated = true;
            return;
        }

        if (Engaged) return;

        float t = Style.Damp(CalibrationRate, Time.unscaledDeltaTime);
        _anchorOffset = Vector3.Lerp(_anchorOffset, offset, t);
        _anchorRotation = Quaternion.Slerp(_anchorRotation, twist, t);
    }

    [ContextMenu("Log Wrist Anchor")]
    private void LogWristAnchor()
    {
        Debug.Log($"[Watch] anchorOffset {_anchorOffset.ToString("F4")} " +
                  $"anchorRotation {_anchorRotation.eulerAngles.ToString("F2")} " +
                  $"(calibrated={_calibrated})");
    }

    private bool TryFingerPose(Pose wrist, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;

        if (!_ihand.GetJointPose(AlongJoint, out Pose middle) ||
            !_ihand.GetJointPose(IndexJoint, out Pose index) ||
            !_ihand.GetJointPose(PinkyJoint, out Pose pinky)) return false;

        Vector3 along = middle.position - wrist.position;
        Vector3 across = index.position - pinky.position;
        if (along.sqrMagnitude < 1e-8f || across.sqrMagnitude < 1e-8f) return false;

        along.Normalize();
        across.Normalize();

        Vector3 outward = Vector3.Cross(along, across).normalized;

        Vector3 stackUp = -across;
        stackUp = Vector3.ProjectOnPlane(stackUp, outward).normalized;
        if (stackUp.sqrMagnitude < 1e-8f) return false;

        Vector3 anchorPosition = Vector3.Lerp(wrist.position, middle.position, HandPlacement);
        rotation = Quaternion.LookRotation(-outward, stackUp);
        position = anchorPosition + outward * yOffsetFromHand;
        return true;
    }

    // Hidden while the hand is away, not switched off, so the buttons are
    // never rebuilt as the hand comes and goes: they stop drawing and taking
    // fingertips.
    private void SetVisible(bool visible)
    {
        if (_canvas == null || _canvas.enabled == visible) return;
        ShowCanvas(visible);
    }

    private void ShowCanvas(bool visible) => UILayout.ShowCanvas(_canvas, _pokes, visible);

    private PokeInteractable[] _pokes = new PokeInteractable[0];

    public void NotifyIntent()
    {
        if (geminiClient != null) geminiClient.NotifyIntent();
    }

    public void NotifyIntentEnded()
    {
        if (geminiClient != null) geminiClient.NotifyIntentEnded();
    }
}
