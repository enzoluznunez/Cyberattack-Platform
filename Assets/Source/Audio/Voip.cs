using System.Runtime.InteropServices;
using AOT;
using UnityEngine.Events;

public static class Voip {

    public static UnityAction<byte[]> FrameCaptured;

#if UNITY_ANDROID && !UNITY_EDITOR

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FnPtr(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)]
        byte[] data,
        int numFrames
    );

    [MonoPInvokeCallback(typeof(FnPtr))]
    private static void CallbackDef(byte[] data, int numFrames) {
        FrameCaptured?.Invoke(data);
    }

    private static FnPtr _callback = CallbackDef;

    [DllImport("voip", EntryPoint = "init")]
    private static extern void Init(FnPtr callbackParam);

    public static void Init() {
        Init(_callback);
    }

    [DllImport("voip", EntryPoint = "destroy")]
    public static extern void Destroy();

    [DllImport("voip", EntryPoint = "start")]
    public static extern void Start();

    [DllImport("voip", EntryPoint = "stop")]
    public static extern void Stop();

#else

    public static void Init() { }
    public static void Destroy() { }
    public static void Start() { }
    public static void Stop() { }

#endif

}
