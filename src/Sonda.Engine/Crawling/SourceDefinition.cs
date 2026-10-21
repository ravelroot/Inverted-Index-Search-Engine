using System.Text;
using System.Text.RegularExpressions;

namespace Sonda.Engine.Crawling;

public sealed record SourceDefinition
{
    public static readonly string[] DefaultInclude = ["*.txt", "*.md", "*.markdown", "*.html", "*.htm"];

    public static readonly string[] DefaultExclude = ["**/.git/**", "**/node_modules/**", "**/bin/**", "**/obj/**"];

    public required string Name { get; init; }

    public required string Path { get; init; }

    public IReadOnlyList<string> Include { get; init; } = DefaultInclude;

    public IReadOnlyList<string> Exclude { get; init; } = DefaultExclude;

    public bool Watch { get; init; }

    public bool FromConfiguration { get; init; }

    public static bool IsValidName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Length <= 40 && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');

    public string KeyPrefix => Name + ":";
}

public sealed class GlobMatcher
{
    private readonly (Regex Pattern, bool PathPattern)[] _include;
    private readonly (Regex Pattern, bool PathPattern)[] _exclude;

    public GlobMatcher(IEnumerable<string> include, IEnumerable<string> exclude)
    {
        _include = include.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Compile).ToArray();
        _exclude = exclude.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Compile).ToArray();
    }

    public bool IsMatch(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        var name = System.IO.Path.GetFileName(path);
        foreach (var (pattern, pathPattern) in _exclude)
        {
            if (pattern.IsMatch(pathPattern ? path : name) || (pathPattern && pattern.IsMatch(path + "/")))
            {
                return false;
            }
        }

        if (_include.Length == 0)
        {
            return true;
        }

        foreach (var (pattern, pathPattern) in _include)
        {
            if (pattern.IsMatch(pathPattern ? path : name))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsExcludedDirectory(string relativeDirectory)
    {
        var path = relativeDirectory.Replace('\\', '/').TrimEnd('/') + "/";
        foreach (var (pattern, pathPattern) in _exclude)
        {
            if (pathPattern && (pattern.IsMatch(path) || pattern.IsMatch(path + "x")))
            {
                return true;
            }
        }

        return false;
    }

    private static (Regex, bool) Compile(string glob)
    {
        var pattern = glob.Trim().Replace('\\', '/');
        var pathPattern = pattern.Contains('/');
        if (pattern.StartsWith("**/", StringComparison.Ordinal))
        {
            pattern = pattern[3..];
            pathPattern = true;
            var body = Translate(pattern);
            return (new Regex("^(?:.*/)?" + body + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), true);
        }

        return (new Regex("^" + Translate(pattern) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), pathPattern);
    }

    private static string Translate(string glob)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    builder.Append(".*");
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++;
                    }
                }
                else
                {
                    builder.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(c.ToString()));
            }
        }

        return builder.ToString();
    }
}
