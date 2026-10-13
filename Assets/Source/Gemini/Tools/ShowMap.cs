using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class ShowMap : AgenticTool<ShowMap.Args> {

    public class Args {
        [Doc("The map: 'United States' for the whole country, or a region, such as 'West' or 'New England'.")]
        public string map;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "ShowMap",
        Description = "Slide the map to the United States or one of its regions, as if the user had slid the map's slider to it. " +
                      "Every map brings its sheet onto the table: a region's, or for the United States every " +
                      "US breach, so this is also how to show another region's bars.",
        Parameters = WithMaps(ParametersFor(typeof(Args)))
    };

    private static Schema WithMaps(Schema schema) {
        var maps = new List<string> { "United States" };
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
        result["showing"] = args.map.Trim();
        result["note"] = "The map slides there and its sheet is opened; row and column numbers " +
                         "change, so read the sheet with DescribeSheet before using them.";
    }
}
