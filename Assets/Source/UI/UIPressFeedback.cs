using System.Collections;
using UnityEngine;

public class UIPressFeedback : MonoBehaviour
{
    private const int SampleRate = 44100;
    private const float ClickFrequency = 1800f;
    private const float ClickSeconds = 0.03f;
    private const float ClickDecay = 90f;
    private const float ClickVolume = 0.4f;
    private const float HapticFrequency = 0.6f;
    private const float HapticAmplitude = 0.35f;
    private const float HapticSeconds = 0.04f;

    // Two fingertips landing together, or a quick second poke, click once.
    private const float MinGapSeconds = 0.06f;
    private float _lastEmit = float.NegativeInfinity;

    private static UIPressFeedback _instance;

    private AudioSource _source;
    private AudioClip _click;

    public static void Play()
    {
        Ensure();
        if (_instance != null) _instance.Emit();
    }

    public static void Prepare() => Ensure();

    private static void Ensure() => Spawn.Once(ref _instance);

    private void Awake()
    {
        if (_instance == null) _instance = this;
        _source = gameObject.AddComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.spatialBlend = 0f;
        _click = BuildClick();
    }

    private AudioClip BuildClick()
    {
        int count = Mathf.CeilToInt(SampleRate * ClickSeconds);
        float[] data = new float[count];
        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float envelope = Mathf.Exp(-t * ClickDecay);
            data[i] = Mathf.Sin(2f * Mathf.PI * ClickFrequency * t) * envelope * ClickVolume;
        }

        AudioClip clip = AudioClip.Create("uiClick", count, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    private void Emit()
    {
        float now = Time.unscaledTime;
        if (now - _lastEmit < MinGapSeconds) return;
        _lastEmit = now;

        if (_source != null && _click != null) _source.PlayOneShot(_click);
        OVRInput.SetControllerVibration(HapticFrequency, HapticAmplitude, OVRInput.Controller.Active);
        StopAllCoroutines();
        StartCoroutine(StopHaptic());
    }

    private IEnumerator StopHaptic()
    {
        yield return new WaitForSecondsRealtime(HapticSeconds);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.Active);
    }
}
