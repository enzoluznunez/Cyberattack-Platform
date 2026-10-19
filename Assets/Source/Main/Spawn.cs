using UnityEngine;

public static class Spawn
{
    // Makes the one T that lives for the whole run, on a GameObject of its
    // own, unless there is one already.
    public static void Once<T>(ref T instance) where T : MonoBehaviour
    {
        if (instance != null) return;
        var go = new GameObject(typeof(T).Name);
        Object.DontDestroyOnLoad(go);
        instance = go.AddComponent<T>();
    }
}
