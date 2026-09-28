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
}
