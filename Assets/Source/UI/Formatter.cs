using System;
using System.Globalization;

public static class Formatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Compact(double value)
    {
        if (double.IsNaN(value)) return "no data";

        bool negative = value < 0.0;
        double abs = Math.Abs(value);

        string body;
        if (abs >= 1e12) body = (abs / 1e12).ToString("0.##", Inv) + "T";
        else if (abs >= 1e9) body = (abs / 1e9).ToString("0.##", Inv) + "B";
        else if (abs >= 1e6) body = (abs / 1e6).ToString("0.##", Inv) + "M";
        else if (abs >= 1e3) body = (abs / 1e3).ToString("0.##", Inv) + "K";
        else body = abs == Math.Floor(abs) ? abs.ToString("N0", Inv) : abs.ToString("N2", Inv);

        return negative ? $"({body})" : body;
    }

    public static string Count(int value)
    {
        bool negative = value < 0;
        string body = Math.Abs((long)value).ToString("N0", Inv);
        return negative ? $"({body})" : body;
    }
}
