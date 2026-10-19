using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class ShowMap : AgenticTool<ShowMap.Args> {

    public class Args {
        [Doc("The map: 'United States' for the whole country, or a region, such as 'West' or 'New England'.")]
        public string map;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "ShowMap",
        Description = "Slide the map to the United States or one of its regions, as if the user had slid the map's Region slider to it. " +
                      "Each map remembers what was last selected on it: the map opens with that selected and its " +
                      "sheet open, or, with nothing selected, its own sheet: the whole country's on the United " +
                      "States map, the whole region's on a region's map. To open a particular metro's or region's " +
                      "sheet, or a map's own whole sheet, call SetDataset.",
        Parameters = WithMaps(ParametersFor(typeof(Args)))
    };

    private static Schema WithMaps(Schema schema) {
        var maps = new List<string> { ViewCatalog.National };
        foreach (string region in CyberContract.Regions) maps.Add(ViewCatalog.Title(region));
        schema.Properties["map"].Enum = maps;
        return schema;
    }

    protected override void Run(Args args, Dictionary<string, object> result) {
        var maps = MapCarousel.Instance;
        if (maps == null) { result["error"] = "There is no map in this scene."; return; }

        if (!maps.Show(args.map, out string reason)) {
            result["error"] = reason;
            return;
        }
        OpenMap(maps, result);
        result["showing"] = args.map.Trim();
        result["note"] = "The map slides there and its sheet is opened; row and column numbers " +
                         "change, so read the sheet with DescribeSheet before using them.";
    }
}
