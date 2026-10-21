using System.Globalization;

namespace Sonda.Cli;

internal sealed class Arguments
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positionals = [];

    private static readonly HashSet<string> BooleanFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "completo", "full", "tfidf", "bm25", "ou", "or", "e", "and", "explicar", "explain", "recentes", "recent",
        "json", "frequencia", "frequency", "ajuda", "help", "sem-trechos", "no-snippets", "manter", "keep",
    };

    public Arguments(IEnumerable<string> args)
    {
        string? pending = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                if (pending is not null)
                {
                    _options[pending] = null;
                }

                var name = arg[2..];
                var equals = name.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                {
                    _options[name[..equals]] = name[(equals + 1)..];
                    pending = null;
                }
                else if (BooleanFlags.Contains(name))
                {
                    _options[name] = null;
                    pending = null;
                }
                else
                {
                    pending = name;
                }
            }
            else if (pending is not null)
            {
                _options[pending] = arg;
                pending = null;
            }
            else
            {
                _positionals.Add(arg);
            }
        }

        if (pending is not null)
        {
            _options[pending] = null;
        }
    }

    public IReadOnlyList<string> Positionals => _positionals;

    public string? Positional(int index) => index < _positionals.Count ? _positionals[index] : null;

    public bool Flag(params string[] names) => names.Any(_options.ContainsKey);

    public string? Get(params string[] names)
    {
        foreach (var name in names)
        {
            if (_options.TryGetValue(name, out var value) && value is not null)
            {
                return value;
            }
        }

        return null;
    }

    public int GetInt(int fallback, params string[] names)
        => int.TryParse(Get(names), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public string[] GetList(string[] fallback, params string[] names)
    {
        var value = Get(names);
        return value is null
            ? fallback
            : value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
