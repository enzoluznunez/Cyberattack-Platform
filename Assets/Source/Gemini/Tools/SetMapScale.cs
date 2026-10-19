using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class SetMapScale : AgenticTool<SetMapScale.Args> {

    public class Args {
        [Doc("How large to draw the maps, against their usual size: 1 is the usual size, 0.25 the smallest, 2.5 the largest.")]
        [Limits(0.25, 2.5)]
        public float scale;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "SetMapScale",
        Description = "Size the maps, as if the user had moved the map's Scale slider: every map, the national " +
                      "and the regions', is drawn at this one size. It changes nothing on the sheet.",
        Parameters = ParametersFor(typeof(Args))
    };

    protected override void Run(Args args, Dictionary<string, object> result) {
        var maps = MapCarousel.Instance;
        if (maps == null) { result["error"] = "There is no map in this scene."; return; }

        float before = maps.MapScale;
        if (!maps.SetScale(args.scale, out string reason)) {
            result["error"] = reason;
            return;
        }
        OpenMap(maps, result);
        result["scale"] = maps.MapScale;
        result["was"] = before;
    }
}
