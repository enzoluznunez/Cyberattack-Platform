using System;
using System.Collections;
using Oculus.Interaction;
using UnityEngine;

public static class PanelPlacement
{
    public const float MinReach = 0.35f;
    public const float MaxReach = 2f;
    public const float FlipMargin = 15f;

    public static Vector3 Reachable(Vector3 target, out bool limited)
    {
        limited = false;

        Transform cam = CameraRig.MainTransform;
        if (cam == null) return target;

        Vector3 delta = target - cam.position;
        float distance = delta.magnitude;
        if (distance < 1e-4f) return target;

        float clamped = Mathf.Clamp(distance, MinReach, MaxReach);
        if (Mathf.Approximately(clamped, distance)) return target;

        limited = true;
        return cam.position + delta / distance * clamped;
    }

    public static bool ShouldFlip(Vector3 position, Vector3 forward, Vector3 viewer)
    {
        Vector3 toViewer = viewer - position;
        toViewer.y = 0f;
        if (toViewer.sqrMagnitude < 1e-6f) return false;

        Vector3 face = forward;
        face.y = 0f;
        if (face.sqrMagnitude < 1e-6f) return false;

        return Vector3.Angle(-face, toViewer) > 90f + FlipMargin;
    }

    public const float FlipSeconds = 0.35f;

    // A two-hand turn on 'go' as far as the hands take it. Reuses the turn
    // 'go' already has.
    public static TwoGrabRotateTransformer FreeSpin(GameObject go)
    {
        TwoGrabRotateTransformer spin = go.GetComponent<TwoGrabRotateTransformer>();
        if (spin != null) return spin;

        spin = go.AddComponent<TwoGrabRotateTransformer>();
        spin.InjectOptionalConstraints(new TwoGrabRotateTransformer.TwoGrabRotateConstraints
        {
            MinAngle = new FloatConstraint(),
            MaxAngle = new FloatConstraint()
        });
        return spin;
    }

    // How a panel turns in two hands: about the upright through 'pivot', as
    // far as the hands take it.
    public static TwoGrabRotateTransformer UprightSpin(GameObject go, Transform pivot)
    {
        TwoGrabRotateTransformer spin = FreeSpin(go);
        spin.InjectOptionalPivotTransform(pivot);
        spin.InjectOptionalRotationAxis(TwoGrabRotateTransformer.Axis.Up);
        return spin;
    }

    // Turns 't' half round about the upright through 'pivot', to face a
    // viewer who has walked round behind it. It gives way, where it stands,
    // the moment 'stop' says so (a hand has taken hold) or anything else
    // places 't' meanwhile: a panel stood somewhere new is not turned back.
    public static IEnumerator Flip(Transform t, Vector3 pivot, Func<bool> stop)
    {
        Quaternion from = t.rotation;
        Quaternion to = Quaternion.AngleAxis(180f, Vector3.up) * from;
        Quaternion half = to * Quaternion.Inverse(from);
        Vector3 offset = t.position - pivot;
        Vector3 wrotePos = t.localPosition;
        Quaternion wroteRot = t.localRotation;

        float elapsed = 0f;
        while (elapsed < FlipSeconds)
        {
            yield return null;
            if (stop() || Moved(t, wrotePos, wroteRot)) yield break;
            elapsed += Time.unscaledDeltaTime;
            Quaternion turn = Quaternion.Slerp(Quaternion.identity, half, Mathf.Clamp01(elapsed / FlipSeconds));
            t.SetPositionAndRotation(pivot + turn * offset, turn * from);
            wrotePos = t.localPosition;
            wroteRot = t.localRotation;
        }
    }

    // Against its parent, so a parent carrying it along does not count.
    private static bool Moved(Transform t, Vector3 localPos, Quaternion localRot) =>
        (t.localPosition - localPos).sqrMagnitude > 1e-8f || Quaternion.Angle(t.localRotation, localRot) > 0.01f;
}

// The swell a grabbed thing takes while a hand holds it, and the scale it
// goes back to when let go.
public struct GrabSwell
{
    private bool _on;
    private Vector3 _rest;

    public bool On => _on;

    // The scale at rest, without the swell.
    public Vector3 Rest(Transform t) => _on ? _rest : t.localScale;

    public void Set(Transform t, bool held)
    {
        if (held == _on) return;
        if (held)
        {
            _rest = t.localScale;
            t.localScale = _rest * Style.EngageScale;
        }
        else t.localScale = _rest;
        _on = held;
    }

    // Takes the present scale as the resting one, swell or not.
    public void Forget(Transform t)
    {
        _on = false;
        _rest = t.localScale;
    }
}
