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

    private const float MaxPitch = 75f;

    // Where the head is and which way it faces across the floor, once the
    // head's pose can be trusted (HeadPose.Settled).
    public static bool TryGetBasis(out Vector3 position, out Quaternion yaw)
    {
        position = Vector3.zero;
        yaw = Quaternion.identity;

        Transform cam = MainTransform;
        if (!HeadPose.Settled || cam == null) return false;

        Vector3 flat = Flatten(cam.forward, Flatten(cam.up, Vector3.zero));
        if (flat == Vector3.zero) return false;

        position = cam.position;
        yaw = Quaternion.LookRotation(flat, Vector3.up);
        return true;
    }

    public static Vector3 Flatten(Vector3 v, Vector3 fallback)
    {
        v.y = 0f;
        return v.sqrMagnitude > 1e-6f ? v.normalized : fallback;
    }

    // Turned to face the viewer, tipped up or down toward their eyes, at
    // most MaxPitch.
    public static bool TryFaceViewer(Vector3 position, out Quaternion rotation)
    {
        rotation = Quaternion.identity;

        Transform cam = MainTransform;
        if (cam == null) return false;

        Vector3 toViewer = cam.position - position;
        Vector3 flat = toViewer;
        flat.y = 0f;
        float horizontal = flat.magnitude;
        if (horizontal < 1e-4f) return false;

        float elevation = Mathf.Atan2(toViewer.y, horizontal) * Mathf.Rad2Deg;
        float tilt = Mathf.Clamp(elevation, -MaxPitch, MaxPitch);

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
