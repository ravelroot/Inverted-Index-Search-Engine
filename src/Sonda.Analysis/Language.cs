namespace Sonda.Analysis;

public enum Language : byte
{
    Unknown = 0,
    Portuguese = 1,
    English = 2,
}

public enum StemmingMode
{
    Auto,
    Portuguese,
    English,
    None,
}

public static class LanguageCodes
{
    public static string ToCode(this Language language) => language switch
    {
        Language.Portuguese => "pt",
        Language.English => "en",
        _ => "xx",
    };

    public static Language FromCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Language.Unknown;
        }

        var normalized = code.Trim().ToLowerInvariant();
        if (normalized.StartsWith("pt", StringComparison.Ordinal) || normalized is "portugues" or "português" or "portuguese")
        {
            return Language.Portuguese;
        }

        if (normalized.StartsWith("en", StringComparison.Ordinal) || normalized is "ingles" or "inglês" or "english")
        {
            return Language.English;
        }

        return Language.Unknown;
    }
}
