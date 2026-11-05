public static class StalePositions
{
    private static bool _rowsDirty;
    private static bool _colsDirty;

    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => ClearAll();

    // Only a reorder the user makes counts; one inside an agent call does not.
    public static void MarkDirty(bool columns)
    {
        if (!StateChannel.UserDriven) return;
        if (columns) _colsDirty = true;
        else _rowsDirty = true;
    }

    public static bool IsDirty(bool columns) => columns ? _colsDirty : _rowsDirty;

    public static void Clear(bool columns)
    {
        if (columns) _colsDirty = false;
        else _rowsDirty = false;
    }

    public static void ClearAll()
    {
        _rowsDirty = false;
        _colsDirty = false;
    }
}
