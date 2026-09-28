public static class PanelGuard
{
    private static bool toolClosedByUser;

    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => toolClosedByUser = false;

    public static bool ToolPanelClosedByUser => toolClosedByUser;

    public static void MarkToolPanelClosedByUser() => toolClosedByUser = true;

    public static void ClearToolPanel() => toolClosedByUser = false;
}
