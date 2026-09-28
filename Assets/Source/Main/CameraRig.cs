using UnityEngine;

public static class CameraRig
{
    private static Transform _cached;

    public static Transform MainTransform
    {
        get
        {
            if (_cached == null)
            {
                Camera cam = Camera.main;
                if (cam != null) _cached = cam.transform;
            }
            return _cached;
        }
    }

    public const float DefaultMaxPitch = 75f;

    public static Vector3 Flatten(Vector3 v, Vector3 fallback)
    {
        v.y = 0f;
        return v.sqrMagnitude > 1e-6f ? v.normalized : fallback;
    }

    public static bool TryFaceViewer(Vector3 position, float extraPitch, bool adaptPitch,
        float maxPitch, out Quaternion rotation)
    {
        rotation = Quaternion.identity;

        Transform cam = MainTransform;
        if (cam == null) return false;

        Vector3 toViewer = cam.position - position;
        Vector3 flat = toViewer;
        flat.y = 0f;
        float horizontal = flat.magnitude;
        if (horizontal < 1e-4f) return false;

        float tilt = extraPitch;
        if (adaptPitch)
        {
            float elevation = Mathf.Atan2(toViewer.y, horizontal) * Mathf.Rad2Deg;
            tilt = Mathf.Clamp(elevation + extraPitch, -maxPitch, maxPitch);
        }

        rotation = Quaternion.LookRotation(-flat / horizontal, Vector3.up)
                   * Quaternion.Euler(tilt, 0f, 0f);
        return true;
    }

    public static Vector3 FlatForward
    {
        get
        {
            Transform cam = MainTransform;
            return cam != null ? Flatten(cam.forward, Vector3.forward) : Vector3.forward;
        }
    }
}
