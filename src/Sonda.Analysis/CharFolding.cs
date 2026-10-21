namespace Sonda.Analysis;

public static class CharFolding
{
    private static readonly char[] FoldTable = BuildTable();

    private static char[] BuildTable()
    {
        var table = new char[0x250];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = char.ToLowerInvariant((char)i);
        }

        void Map(string sources, char target)
        {
            foreach (var source in sources)
            {
                table[source] = target;
                var upper = char.ToUpperInvariant(source);
                if (upper < table.Length)
                {
                    table[upper] = target;
                }
            }
        }

        Map("àáâãäåāăą", 'a');
        Map("çćĉċč", 'c');
        Map("ďđ", 'd');
        Map("èéêëēĕėęě", 'e');
        Map("ĝğġģ", 'g');
        Map("ĥħ", 'h');
        Map("ìíîïĩīĭįı", 'i');
        Map("ĵ", 'j');
        Map("ķ", 'k');
        Map("ĺļľŀł", 'l');
        Map("ñńņňŉ", 'n');
        Map("òóôõöøōŏő", 'o');
        Map("ŕŗř", 'r');
        Map("śŝşš", 's');
        Map("ţťŧ", 't');
        Map("ùúûüũūŭůűų", 'u');
        Map("ŵ", 'w');
        Map("ýÿŷ", 'y');
        Map("źżž", 'z');
        return table;
    }

    public static char Fold(char c) => c < FoldTable.Length ? FoldTable[c] : char.ToLowerInvariant(c);

    public static void FoldInPlace(Span<char> text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            text[i] = Fold(text[i]);
        }
    }

    public static string Fold(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = Fold(source[i]);
            }
        });
    }

    public static void LowerInto(ReadOnlySpan<char> source, Span<char> destination)
    {
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            destination[i] = c < 128 ? (char)(c is >= 'A' and <= 'Z' ? c + 32 : c) : char.ToLowerInvariant(c);
        }
    }

    public static bool HasDiacritics(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (c >= 128 && Fold(c) != char.ToLowerInvariant(c))
            {
                return true;
            }
        }

        return false;
    }
}
