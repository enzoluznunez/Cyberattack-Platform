using System;
using System.Globalization;

public static class Formatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Count(int value)
    {
        bool negative = value < 0;
        string body = Math.Abs((long)value).ToString("N0", Inv);
        return negative ? $"({body})" : body;
    }
}
