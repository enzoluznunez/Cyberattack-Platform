using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

public class PointerHighlight : MonoBehaviour,
    IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
{
    private const float LiftDuration = 0.08f;

    public Graphic target;
    public Color restColor;
    public Color pressColor;

    public Transform liftTarget;

    public Graphic textTarget;
    public Color textRestColor;
    public Color textPressColor;

    private bool _hovered;
    private bool _pressed;
    private Vector3 _baseScale = Vector3.one;
    private float _liftT;
    private Coroutine _lift;

    private void Awake()
    {
        _baseScale = transform.localScale;
    }

    public void SetLiftTarget(Transform t)
    {
        liftTarget = t;
        _baseScale = (t != null ? t : transform).localScale;
    }

    public void SetTextTarget(Graphic text) => textTarget = text;

    public bool IsEngaged => _hovered || _pressed;

    public void SetState(Color rest, Color press, Color textRest, Color textPress)
    {
        restColor = rest;
        pressColor = press;
        textRestColor = textRest;
        textPressColor = textPress;
        Apply();
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        _hovered = true;
        Apply();
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        _hovered = false;
        _pressed = false;
        Apply();
        DriveLift();
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        _pressed = true;
        Apply();
        DriveLift();
        UIPressFeedback.Play();
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        _pressed = false;
        Apply();
        DriveLift();
    }

    private void DriveLift()
    {
        if (!isActiveAndEnabled)
        {
            SnapLift();
            return;
        }
        if (Mathf.Approximately(_liftT, _pressed ? 1f : 0f)) return;
        if (_lift == null) _lift = StartCoroutine(LiftRoutine());
    }

    private IEnumerator LiftRoutine()
    {
        while (true)
        {
            float goal = _pressed ? 1f : 0f;
            if (Mathf.Approximately(_liftT, goal)) break;

            _liftT = Mathf.MoveTowards(_liftT, goal, Time.unscaledDeltaTime / LiftDuration);
            ApplyLift();
            yield return null;
        }
        _lift = null;
    }

    private void SnapLift()
    {
        _liftT = _pressed ? 1f : 0f;
        ApplyLift();
    }

    private void ApplyLift()
    {
        Transform xform = liftTarget != null ? liftTarget : transform;
        xform.localScale = _baseScale * Mathf.Lerp(1f, Style.EngageScale, _liftT);
    }

    private void OnDisable()
    {
        _lift = null;
        _hovered = false;
        _pressed = false;
        SnapLift();
    }

    private void Apply()
    {
        if (target != null)
            target.color = _pressed ? pressColor : restColor;
        if (textTarget != null)
            textTarget.color = _pressed ? textPressColor : textRestColor;
    }
}
