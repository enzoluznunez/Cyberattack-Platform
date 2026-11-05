using System.Collections.Generic;

public static class StateChannel
{
    private const int MaxUserQueue = 40;

    private static int _agentDepth;

    public static bool InAgentCall
    {
        get => _agentDepth > 0;
        set
        {
            if (value) _agentDepth++;
            else if (_agentDepth > 0) _agentDepth--;
        }
    }

    public static bool UserDriven => !InAgentCall;

    private static readonly List<string> AgentBatch = new List<string>();
    private static readonly List<string> UserQueue = new List<string>();
    private static readonly Dictionary<string, string> State = new Dictionary<string, string>();
    private static readonly List<string> StateOrder = new List<string>();
    private static bool _snapshotWanted;

    public static void Record(string what)
    {
        if (string.IsNullOrEmpty(what)) return;

        if (InAgentCall)
        {
            AgentBatch.Add(what);
            return;
        }


        UserQueue.Add(what);
        while (UserQueue.Count > MaxUserQueue) UserQueue.RemoveAt(0);
        Gemini.RequestActionPush();
    }

    public static string TakeAgentBatch()
    {
        if (AgentBatch.Count == 0) return null;
        string text = string.Join("; ", AgentBatch);
        AgentBatch.Clear();
        return text;
    }

    public static void SetState(string key, string what)
    {
        if (string.IsNullOrEmpty(what)) return;
        if (!State.ContainsKey(key)) StateOrder.Add(key);
        State[key] = what;
    }

    public static void RecordState(string key, string what)
    {
        SetState(key, what);
        Record(what);
    }

    public static void ClearPending()
    {
        UserQueue.Clear();
        AgentBatch.Clear();
        _agentDepth = 0;
        _snapshotWanted = false;
        StalePositions.ClearAll();
    }

    public static void RequestSnapshot()
    {
        _snapshotWanted = true;
        Gemini.RequestActionPush();
    }

    public static bool HasPending => _snapshotWanted || UserQueue.Count > 0;

    public static bool TryTakeBatch(out string text)
    {
        text = null;
        if (!HasPending) return false;

        var lines = new List<string>(2);

        if (_snapshotWanted)
        {
            _snapshotWanted = false;
            if (StateOrder.Count > 0)
            {
                var facts = new List<string>(StateOrder.Count);
                for (int i = 0; i < StateOrder.Count; i++) facts.Add(State[StateOrder[i]]);
                lines.Add("[state] " + string.Join("; ", facts));
            }
        }

        if (UserQueue.Count > 0)
        {
            lines.Add("[tool] User: " + string.Join("; ", UserQueue));
            UserQueue.Clear();
        }

        if (lines.Count == 0) return false;
        text = string.Join("\n", lines);
        return true;
    }
}
