using System.Collections.Generic;
using Google.GenAI.Types;

public sealed class ShowCountry : AgenticTool<ShowCountry.Args> {

    public class Args {
        [Doc("The country, as the map names it, such as 'Japan' or 'United Kingdom'.")]
        public string country;
    }

    public override FunctionDeclaration Declaration => new FunctionDeclaration {
        Name = "ShowCountry",
        Description = "Slide the map to a country, as if the user had swiped to it. The sheets follow the map: every " +
                      "listed sheet counts only the breaches at companies headquartered in the country it shows, so " +
                      "this is also how to show another country's bars. Only countries where a breached company is " +
                      "headquartered have a map.",
        Parameters = WithCountries(ParametersFor(typeof(Args)))
    };

    private static Schema WithCountries(Schema schema) {
        schema.Properties["country"].Enum = new List<string>(CyberContract.Countries);
        return schema;
    }

    protected override void Run(Args args, Dictionary<string, object> result) {
        var maps = MapCarousel.Instance;
        if (maps == null) { result["error"] = "There is no map in this scene."; return; }

        if (!maps.Show(args.country, out string reason)) {
            result["error"] = reason;
            return;
        }
        result["showing"] = args.country.Trim();
        result["note"] = "The map slides there and the sheets are reread for it; row and column numbers may change, " +
                         "so read the sheet with DescribeSheet before using them.";
    }
}
