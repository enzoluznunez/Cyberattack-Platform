public static class StalePositions
{
    private static bool rowsDirty;
    private static bool colsDirty;

    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => ClearAll();

    public static void MarkDirty(bool columns)
    {
        if (columns) colsDirty = true;
        else rowsDirty = true;
    }

    public static bool IsDirty(bool columns) => columns ? colsDirty : rowsDirty;

    public static void Clear(bool columns)
    {
        if (columns) colsDirty = false;
        else rowsDirty = false;
    }

    public static void ClearAll()
    {
        rowsDirty = false;
        colsDirty = false;
    }
}
