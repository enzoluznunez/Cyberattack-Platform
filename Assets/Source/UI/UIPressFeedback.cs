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

    private static UIPressFeedback _instance;

    private AudioSource _source;
    private AudioClip _click;

    public static void Play()
    {
        Ensure();
        if (_instance != null) _instance.Emit();
    }

    private static void Ensure()
    {
        if (_instance != null) return;
        GameObject go = new GameObject("UIPressFeedback");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<UIPressFeedback>();
    }

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
