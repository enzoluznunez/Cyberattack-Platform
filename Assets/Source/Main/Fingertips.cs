using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using UnityEngine;

// The hands' poke interactors, each paired with its hand, for everything that
// reacts to a fingertip: the sheets and the maps. The scene is searched on a
// timer alone, so with no hands tracked it is not searched every frame, and
// every reader shares the one search.
public static class Fingertips
{
    private const float RescanSeconds = 2f;

    public struct Source
    {
        public PokeInteractor poke;
        public IHand hand;

        // Enabled, and on a hand that is tracked now.
        public bool Live => poke != null && poke.isActiveAndEnabled && hand != null && hand.IsTrackedDataValid;
    }

    private static readonly List<Source> _all = new List<Source>();
    private static float _nextScan;

    public static IReadOnlyList<Source> All
    {
        get
        {
            if (Time.unscaledTime >= _nextScan && !BothFound()) Rescan();
            return _all;
        }
    }

    // Both hands' pokes found and still there: nothing more to look for.
    private static bool BothFound()
    {
        if (_all.Count < 2) return false;
        for (int i = 0; i < _all.Count; i++)
            if (_all[i].poke == null) return false;
        return true;
    }

    private static void Rescan()
    {
        _nextScan = Time.unscaledTime + RescanSeconds;
        _all.Clear();
        foreach (PokeInteractor poke in Object.FindObjectsByType<PokeInteractor>(FindObjectsSortMode.None))
        {
            IHand hand = poke.GetComponentInParent<IHand>();
            if (hand != null) _all.Add(new Source { poke = poke, hand = hand });
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _all.Clear();
        _nextScan = 0f;
    }
}
