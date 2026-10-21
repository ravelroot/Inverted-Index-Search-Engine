using System.Globalization;
using Sonda.Engine.Search;

namespace Sonda.Cli;

internal static class Output
{
    public static void Title(string text)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }

    public static void Line(string text = "") => Console.WriteLine(text);

    public static void Dim(string text)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }

    public static void Warning(string text)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }

    public static void Error(string text)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(text);
        Console.ForegroundColor = previous;
    }

    public static void Fragments(IReadOnlyList<HighlightFragment> fragments, ConsoleColor color)
    {
        var previous = Console.ForegroundColor;
        foreach (var fragment in fragments)
        {
            Console.ForegroundColor = fragment.Match ? ConsoleColor.Cyan : color;
            Console.Write(fragment.Text);
        }

        Console.ForegroundColor = previous;
    }

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : value.ToString("0.0", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public static void Table(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        var widths = headers.Select(h => h.Length).ToArray();
        foreach (var row in rows)
        {
            for (var i = 0; i < Math.Min(row.Length, widths.Length); i++)
            {
                widths[i] = Math.Max(widths[i], Math.Min(row[i].Length, 90));
            }
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))));
        Console.WriteLine(string.Join("  ", widths.Select(w => new string('─', w))));
        Console.ForegroundColor = previous;
        foreach (var row in rows)
        {
            Console.WriteLine(string.Join("  ", row.Select((cell, i) => Truncate(cell, 90).PadRight(i < widths.Length ? widths[i] : 0))).TrimEnd());
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
