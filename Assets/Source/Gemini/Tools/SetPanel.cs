using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class SetPanel : AgenticTool<SetPanel.Args> {

    public class Args {
        [Doc("What to do with it. Prefer 'open' or 'close' over 'toggle' when you know the state you want.")]
        [Values("open", "close", "toggle")]
        public string state;
        [Doc("Which panel: 'tool', the tool panel, or 'map', the map panel. Defaults to 'tool'.")]
        [Values("tool", "map"), Optional]
        public string panel;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "SetPanel",
        Description = "Open, close, or toggle the tool panel (the tool buttons and your speed setting) or the " +
                      "map panel, as the user's wrist buttons do. A panel opens ahead of the user, beside the " +
                      "other when that one is open; neither moves or hides the sheet.",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        string state = args.state;

        if (args.panel == "map") {
            var maps = MapCarousel.Instance;
            if (maps == null) { result["error"] = "There is no map in this scene."; return; }
            Apply(state, maps.IsOpen, () => maps.SetOpen(true), () => maps.SetOpen(false), maps.Toggle);
            result["visible"] = maps.IsOpen;
            result["did"] = maps.IsOpen ? "the map panel is open" : "the map panel is closed";
            return;
        }

        var tool = Scene.ToolPanel;
        if (tool == null) { result["error"] = "Tool panel not found in scene."; return; }

        Apply(state, tool.IsVisible, tool.ShowPanel, tool.HidePanel, tool.TogglePanel);
        result["visible"] = tool.IsVisible;
        result["did"] = tool.IsVisible ? "the tool panel is open" : "the tool panel is closed";
    }

    private static void Apply(string state, bool visible, System.Action show, System.Action hide, System.Action toggle) {
        switch (state) {
            case "open": if (!visible) show(); break;
            case "close": if (visible) hide(); break;
            default: toggle(); break;
        }
    }
}
