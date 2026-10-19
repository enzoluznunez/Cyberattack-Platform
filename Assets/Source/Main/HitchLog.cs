using System.Collections.Generic;
using System.Text;
using UnityEngine;

// Logs every frame that runs long, with what was going on around it, so a
// session's stutter can be read back from the headset's log over USB
// ('adb logcat -s Unity' and look for [Hitch]). A frame counts as long past
// one and a half of the display's frame time. Each line says how long the
// frame took; how much of the recent frames the main thread, the render
// thread and the GPU took, which tells work in scripts from work drawing;
// whether the garbage collector ran; and what changed in the half second
// before it. Every few seconds a summary line gives the steady frame rate.
//
// Other code marks what it does with Note, and a note made within the half
// second before a hitch is printed with it.
public class HitchLog : MonoBehaviour
{
    private const float HitchShare = 1.5f;
    private const float NoteWindow = 0.5f;
    private const float SummarySeconds = 10f;
    private const int TimingDepth = 4;

    private static HitchLog _instance;
    private static readonly List<(float at, string what)> Notes = new List<(float, string)>();

    private readonly FrameTiming[] _timings = new FrameTiming[TimingDepth];
    private readonly StringBuilder _line = new StringBuilder(256);

    private int _gcCount;
    private float _frameMs = 1000f / 72f;

    // What was last seen, so a change can be noted.
    private ToolType _tool = ToolType.None;
    private int _dataset = int.MinValue;
    private bool _panel, _map, _agent;

    private float _summaryStart;
    private int _frames, _hitches;
    private float _totalMs, _worstMs;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _instance = null;
        Notes.Clear();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (_instance != null) return;

        // Plain log lines without a stack trace: a development build would
        // otherwise capture one as a string for every line, the assistant's
        // log of each message it receives among them, and that is both time
        // and garbage the collector later stops the app to sweep up.
        // Warnings and errors keep theirs.
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        Spawn.Once(ref _instance);
    }

    // Marks something the app just did, to be printed with any hitch that
    // follows within half a second.
    public static void Note(string what)
    {
        if (_instance == null) return;
        Notes.Add((Time.unscaledTime, what));
        if (Notes.Count > 32) Notes.RemoveAt(0);
    }

    private void Start()
    {
        _gcCount = System.GC.CollectionCount(0);
        _summaryStart = Time.unscaledTime;
        Debug.Log($"[Hitch] Logging frames past {HitchShare:0.#}x the display's frame time.");
    }

    private void Update()
    {
        FrameTimingManager.CaptureFrameTimings();
        WatchState();

        float refresh = DisplayHz();
        if (refresh > 1f) _frameMs = 1000f / refresh;

        // This frame's delta is how long the frame before it took.
        float ms = Time.unscaledDeltaTime * 1000f;
        int gc = System.GC.CollectionCount(0);
        bool collected = gc != _gcCount;
        _gcCount = gc;

        // The first frames after launch and a frame after the app was paused
        // (headset off) are not stutter.
        if (Time.frameCount > 2 && ms < 2000f)
        {
            _frames++;
            _totalMs += ms;
            if (ms > _worstMs) _worstMs = ms;
            if (ms > _frameMs * HitchShare)
            {
                _hitches++;
                LogHitch(ms, collected);
            }
        }

        if (Time.unscaledTime - _summaryStart >= SummarySeconds) LogSummary(refresh);
    }

    private static float DisplayHz()
    {
        try { return OVRPlugin.systemDisplayFrequency; }
        catch { return 0f; }
    }

    // Notes the changes a hitch could follow from that nothing else marks:
    // the tool picked, the dataset shown, either panel opening or closing,
    // and the assistant starting or finishing a call.
    private void WatchState()
    {
        ManageTools tools = Scene.Tools;
        ToolType tool = tools != null ? tools.SelectedTool : ToolType.None;
        if (tool != _tool) { _tool = tool; Note($"tool -> {tool}"); }

        ManageDatasets datasets = ManageDatasets.Instance;
        int dataset = datasets != null ? datasets.ActiveIndex : -1;
        if (dataset != _dataset)
        {
            bool first = _dataset == int.MinValue;
            _dataset = dataset;
            if (!first) Note($"dataset -> {Scene.DatasetLabel ?? dataset.ToString()}");
        }

        ToolPanelUI panel = Scene.ToolPanel;
        bool panelOpen = panel != null && panel.IsVisible;
        if (panelOpen != _panel) { _panel = panelOpen; Note(panelOpen ? "tool panel opened" : "tool panel closed"); }

        MapCarousel map = MapCarousel.Instance;
        bool mapOpen = map != null && map.IsOpen;
        if (mapOpen != _map) { _map = mapOpen; Note(mapOpen ? "map panel opened" : "map panel closed"); }

        bool agent = StateChannel.InAgentCall;
        if (agent != _agent) { _agent = agent; Note(agent ? "assistant call started" : "assistant call ended"); }
    }

    private void LogHitch(float ms, bool collected)
    {
        _line.Clear();
        _line.Append("[Hitch] ").Append(ms.ToString("F1")).Append(" ms (")
             .Append((ms / _frameMs).ToString("F1")).Append(" frames)");

        AppendTimings();
        if (collected) _line.Append(" | GC ran");

        _line.Append(" | tool ").Append(_tool);
        ManageSheets sheets = Scene.Sheets;
        if (sheets != null)
        {
            CreateSheet sheet = sheets.Sheet;
            if (sheet != null && sheet.IsGrabbed) _line.Append(", sheet held");
            if (sheets.LinesDragged) _line.Append(", sort drag");
        }
        if (FlatSlideBar.AnyBusy) _line.Append(", map slider in hand");
        if (_agent) _line.Append(", assistant working");

        float now = Time.unscaledTime;
        bool any = false;
        for (int i = 0; i < Notes.Count; i++)
        {
            if (now - Notes[i].at > NoteWindow) continue;
            _line.Append(any ? "; " : " | just before: ").Append(Notes[i].what);
            any = true;
        }

        Debug.Log(_line.ToString());
    }

    // The most each part of the frame took over the last few frames the
    // timings have reached (they trail by a frame or two). Zero where the
    // platform does not report a part.
    private void AppendTimings()
    {
        uint got = FrameTimingManager.GetLatestTimings(TimingDepth, _timings);
        if (got == 0) return;

        double main = 0, render = 0, gpu = 0;
        for (int i = 0; i < got; i++)
        {
            if (_timings[i].cpuMainThreadFrameTime > main) main = _timings[i].cpuMainThreadFrameTime;
            if (_timings[i].cpuRenderThreadFrameTime > render) render = _timings[i].cpuRenderThreadFrameTime;
            if (_timings[i].gpuFrameTime > gpu) gpu = _timings[i].gpuFrameTime;
        }
        _line.Append(" | main ").Append(main.ToString("F1"))
             .Append(", render ").Append(render.ToString("F1"))
             .Append(", gpu ").Append(gpu.ToString("F1"));
    }

    private void LogSummary(float refresh)
    {
        if (_frames > 0)
        {
            float avg = _totalMs / _frames;
            Debug.Log($"[Hitch] Last {SummarySeconds:0} s: {_frames} frames, avg {avg:F1} ms ({1000f / avg:F0} fps " +
                  $"of {refresh:F0} Hz), worst {_worstMs:F1} ms, {_hitches} long.");
        }
        _summaryStart = Time.unscaledTime;
        _frames = _hitches = 0;
        _totalMs = _worstMs = 0f;
    }
}
