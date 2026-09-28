using UnityEngine;

public class OneEuroPoseFilter
{
    private const float DerivativeCutoff = 1f;

    public float posMinCutoff = 1f;
    public float posBeta = 0.02f;
    public float rotMinCutoff = 1f;
    public float rotBeta = 0.05f;

    private bool _hasPrevious;
    private Vector3 _position;
    private Vector3 _velocity;
    private Quaternion _rotation = Quaternion.identity;
    private float _angularSpeed;

    public void Reset() => _hasPrevious = false;

    public Pose Filter(Pose raw, float dt, float hardening = 1f)
    {
        if (!_hasPrevious || dt <= 0f)
        {
            _hasPrevious = true;
            _position = raw.position;
            _rotation = raw.rotation;
            _velocity = Vector3.zero;
            _angularSpeed = 0f;
            return raw;
        }

        Vector3 rawVelocity = (raw.position - _position) / dt;
        _velocity = Vector3.Lerp(_velocity, rawVelocity, Alpha(DerivativeCutoff, dt));
        float posCutoff = (posMinCutoff + posBeta * _velocity.magnitude) * hardening;
        _position = Vector3.Lerp(_position, raw.position, Alpha(posCutoff, dt));

        float rawAngularSpeed = Quaternion.Angle(_rotation, raw.rotation) / dt;
        _angularSpeed = Mathf.Lerp(_angularSpeed, rawAngularSpeed, Alpha(DerivativeCutoff, dt));
        float rotCutoff = (rotMinCutoff + rotBeta * _angularSpeed) * hardening;
        _rotation = Quaternion.Slerp(_rotation, raw.rotation, Alpha(rotCutoff, dt));

        return new Pose(_position, _rotation);
    }

    private static float Alpha(float cutoff, float dt)
    {
        float tau = 1f / (2f * Mathf.PI * Mathf.Max(cutoff, 1e-4f));
        return 1f / (1f + tau / dt);
    }
}
