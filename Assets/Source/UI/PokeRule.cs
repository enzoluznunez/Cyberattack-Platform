// How a fingertip presses anything flat, the same everywhere: the tool
// panel's and the wrist's buttons (Meta's canvas poke), and what the app
// works out for itself, the map's regions and pins and its sliders. A
// fingertip has to come from in front, within HoverDepth of the face, and
// presses on reaching the face; pushed on through, it is still pressing
// down to BehindDepth. Across the face, a press counts up to EdgeSlack
// outside what it lands near. All in metres.
public static class PokeRule
{
    // The panels' hover distance (the canvas poke's enter-hover normal).
    public const float HoverDepth = 0.03f;

    // At the face, as the panels press.
    public const float PressDepth = 0f;

    public const float BehindDepth = 0.08f;

    // Drawn back this far in front of the face, a fingertip holding a slider
    // lets go of it.
    public const float ReleaseDepth = 0.025f;

    // Half the tightest gap between two of the panel's buttons, so a press in
    // the gap still lands, on the nearer one, and two never overlap.
    public const float EdgeSlack = 0.003f;

    public const float Millimetres = 1000f;
}
