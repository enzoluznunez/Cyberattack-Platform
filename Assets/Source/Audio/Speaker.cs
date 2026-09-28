using System.Runtime.InteropServices;

public static class Speaker {

#if UNITY_ANDROID && !UNITY_EDITOR

    [DllImport("spkr", EntryPoint = "init")]
    public static extern void init();

    [DllImport("spkr", EntryPoint = "destroy")]
    public static extern void destroy();

    [DllImport("spkr", EntryPoint = "start")]
    public static extern void start();

    [DllImport("spkr", EntryPoint = "stop")]
    public static extern void stop();

    [DllImport("spkr", EntryPoint = "clear")]
    public static extern void clear();

    [DllImport("spkr", EntryPoint = "play")]
    private static extern void play(
        [MarshalAs(UnmanagedType.LPArray)]
        byte[] data,
        int arrLength
    );

    public static void write(byte[] data) {
        play(data, data.Length);
    }

    public static void flush() {
        stop();
        clear();
        start();
    }

#else

    public static void init() { }
    public static void destroy() { }
    public static void start() { }
    public static void stop() { }
    public static void clear() { }
    public static void write(byte[] data) { }
    public static void flush() { }

#endif

}
