using System.Globalization;
using Sonda.Analysis;
using Sonda.Engine.Index;

namespace Sonda.Engine.Search;

public enum DefaultOperator
{
    And,
    Or,
}

public sealed class QueryParseException(string message) : Exception(message);

public sealed record ParsedQuery(Query Query, bool CanRelax);

public sealed class QueryParser
{
    private readonly TextAnalyzer _analyzer;
    private readonly IReadOnlyList<(int Field, float Boost)> _defaultFields;

    public QueryParser(TextAnalyzer analyzer, IReadOnlyList<(int Field, float Boost)>? defaultFields = null)
    {
        _analyzer = analyzer;
        _defaultFields = defaultFields ?? [(Schema.Title, 2f), (Schema.Body, 1f)];
    }

    public ParsedQuery Parse(string text, DefaultOperator defaultOperator = DefaultOperator.And)
    {
        var tokens = Lex(text ?? string.Empty);
        var plain = tokens.Count > 1 && tokens.All(t => t.Kind == TokenKind.Word && !t.Text.EndsWith('*') && !t.Text.Contains('~') && !t.Text.Contains('^'));
        if (tokens.Count == 0)
        {
            return new ParsedQuery(new MatchNoDocsQuery("consulta vazia"), false);
        }

        var state = new ParserState(tokens);
        var node = ParseOr(state);
        if (!state.AtEnd)
        {
            throw new QueryParseException($"Parêntese ou termo inesperado: \"{state.Peek.Text}\".");
        }

        var query = Build(node, null, defaultOperator) ?? new MatchNoDocsQuery("apenas palavras muito comuns");
        return new ParsedQuery(query, plain);
    }

    private enum TokenKind
    {
        Word,
        Phrase,
        LeftParen,
        RightParen,
        And,
        Or,
        Not,
        Plus,
        Minus,
        Field,
        Modifier,
    }

    private sealed record LexToken(TokenKind Kind, string Text);

    private abstract record Node;

    private sealed record WordNode(string Text, bool Prefix, int? FuzzyEdits, bool Fuzzy, float Boost) : Node;

    private sealed record PhraseNode(string Text, int Slop, float Boost) : Node;

    private sealed record FieldNode(int Field, Node Inner) : Node;

    private sealed record GroupNode(Node Inner, float Boost) : Node;

    private sealed record BoolNode(List<(Node Node, Occur? Occur)> Clauses, bool Disjunction, bool ExplicitAnd) : Node;

    private sealed class ParserState(List<LexToken> tokens)
    {
        private int _position;

        public bool AtEnd => _position >= tokens.Count;

        public LexToken Peek => tokens[_position];

        public LexToken? PeekAt(int offset) => _position + offset < tokens.Count ? tokens[_position + offset] : null;

        public LexToken Next() => tokens[_position++];
    }

    private static List<LexToken> Lex(string text)
    {
        var tokens = new List<LexToken>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            switch (c)
            {
                case '(':
                    tokens.Add(new LexToken(TokenKind.LeftParen, "("));
                    i++;
                    continue;
                case ')':
                    tokens.Add(new LexToken(TokenKind.RightParen, ")"));
                    i++;
                    if (i < text.Length && text[i] == '^')
                    {
                        var start = i;
                        i++;
                        while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
                        {
                            i++;
                        }

                        tokens.Add(new LexToken(TokenKind.Modifier, text[start..i]));
                    }

                    continue;
                case '"':
                case '“':
                case '”':
                {
                    var end = text.IndexOfAny(['"', '”', '“'], i + 1);
                    var phrase = end < 0 ? text[(i + 1)..] : text[(i + 1)..end];
                    i = end < 0 ? text.Length : end + 1;
                    var modifierStart = i;
                    while (i < text.Length && (text[i] == '~' || text[i] == '^' || text[i] == '.' || char.IsAsciiDigit(text[i])))
                    {
                        i++;
                    }

                    tokens.Add(new LexToken(TokenKind.Phrase, phrase + "\u0000" + text[modifierStart..i]));
                    continue;
                }

                case '+' when IsPrefixOperator(text, i):
                    tokens.Add(new LexToken(TokenKind.Plus, "+"));
                    i++;
                    continue;
                case '-' when IsPrefixOperator(text, i):
                case '!' when IsPrefixOperator(text, i):
                    tokens.Add(new LexToken(TokenKind.Minus, "-"));
                    i++;
                    continue;
            }

            if (Matches(text, i, "&&"))
            {
                tokens.Add(new LexToken(TokenKind.And, "&&"));
                i += 2;
                continue;
            }

            if (Matches(text, i, "||"))
            {
                tokens.Add(new LexToken(TokenKind.Or, "||"));
                i += 2;
                continue;
            }

            var wordStart = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not '(' and not ')' and not '"' and not '“' and not '”')
            {
                if (text[i] == ':' && i > wordStart)
                {
                    break;
                }

                i++;
            }

            var word = text[wordStart..i];
            if (i < text.Length && text[i] == ':' && word.Length > 0 && Schema.TryResolveField(word, out _))
            {
                tokens.Add(new LexToken(TokenKind.Field, word));
                i++;
                continue;
            }

            if (i < text.Length && text[i] == ':')
            {
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not '(' and not ')')
                {
                    i++;
                }

                word = text[wordStart..i];
            }

            switch (word)
            {
                case "AND" or "E":
                    tokens.Add(new LexToken(TokenKind.And, word));
                    break;
                case "OR" or "OU":
                    tokens.Add(new LexToken(TokenKind.Or, word));
                    break;
                case "NOT" or "NAO" or "NÃO":
                    tokens.Add(new LexToken(TokenKind.Not, word));
                    break;
                default:
                    if (word.Length > 0)
                    {
                        tokens.Add(new LexToken(TokenKind.Word, word));
                    }

                    break;
            }
        }

        return tokens;
    }

    private static bool IsPrefixOperator(string text, int index)
        => (index == 0 || char.IsWhiteSpace(text[index - 1]) || text[index - 1] == '(')
            && index + 1 < text.Length && !char.IsWhiteSpace(text[index + 1]);

    private static bool Matches(string text, int index, string value)
        => index + value.Length <= text.Length && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

    private Node ParseOr(ParserState state)
    {
        var first = ParseAnd(state);
        if (state.AtEnd || state.Peek.Kind != TokenKind.Or)
        {
            return first;
        }

        var clauses = new List<(Node, Occur?)> { (first, Occur.Should) };
        while (!state.AtEnd && state.Peek.Kind == TokenKind.Or)
        {
            var operatorToken = state.Next();
            if (state.AtEnd || state.Peek.Kind == TokenKind.RightParen)
            {
                throw new QueryParseException($"Faltou um termo depois de {operatorToken.Text}.");
            }

            clauses.Add((ParseAnd(state), Occur.Should));
        }

        return new BoolNode(clauses, true, false);
    }

    private Node ParseAnd(ParserState state)
    {
        var clauses = new List<(Node, Occur?)>();
        var explicitAnd = false;
        while (!state.AtEnd)
        {
            var token = state.Peek;
            if (token.Kind is TokenKind.Or or TokenKind.RightParen)
            {
                break;
            }

            if (token.Kind == TokenKind.And)
            {
                explicitAnd = true;
                state.Next();
                continue;
            }

            clauses.Add(ParseUnary(state));
        }

        if (clauses.Count == 0)
        {
            throw new QueryParseException("Faltou um termo na consulta.");
        }

        return clauses.Count == 1 && clauses[0].Item2 is null ? clauses[0].Item1 : new BoolNode(clauses, false, explicitAnd);
    }

    private (Node, Occur?) ParseUnary(ParserState state)
    {
        Occur? occur = null;
        var token = state.Peek;
        if (token.Kind == TokenKind.Plus)
        {
            occur = Occur.Must;
            state.Next();
        }
        else if (token.Kind is TokenKind.Minus or TokenKind.Not)
        {
            occur = Occur.MustNot;
            state.Next();
        }

        if (state.AtEnd)
        {
            throw new QueryParseException("Operador sem termo depois dele.");
        }

        return (ParsePrimary(state), occur);
    }

    private Node ParsePrimary(ParserState state)
    {
        var token = state.Next();
        switch (token.Kind)
        {
            case TokenKind.LeftParen:
            {
                var inner = ParseOr(state);
                if (state.AtEnd || state.Peek.Kind != TokenKind.RightParen)
                {
                    throw new QueryParseException("Faltou fechar um parêntese.");
                }

                state.Next();
                var boost = 1f;
                if (!state.AtEnd && state.Peek.Kind == TokenKind.Modifier)
                {
                    boost = ParseFloat(state.Next().Text[1..], 1f);
                }

                return new GroupNode(inner, boost);
            }

            case TokenKind.Field:
            {
                Schema.TryResolveField(token.Text, out var field);
                if (state.AtEnd)
                {
                    throw new QueryParseException($"Faltou o valor depois de {token.Text}:.");
                }

                return new FieldNode(field, ParsePrimary(state));
            }

            case TokenKind.Phrase:
            {
                var separator = token.Text.IndexOf('\u0000', StringComparison.Ordinal);
                var phrase = token.Text[..separator];
                var modifiers = token.Text[(separator + 1)..];
                ParseModifiers(modifiers, out _, out var slop, out var boost);
                return new PhraseNode(phrase, slop ?? 0, boost);
            }

            case TokenKind.Word:
            {
                var text = token.Text;
                var prefix = false;
                var fuzzy = false;
                int? edits = null;
                var boost = 1f;
                var caret = text.LastIndexOf('^');
                if (caret > 0)
                {
                    boost = ParseFloat(text[(caret + 1)..], 1f);
                    text = text[..caret];
                }

                var tilde = text.LastIndexOf('~');
                if (tilde > 0)
                {
                    fuzzy = true;
                    var digits = text[(tilde + 1)..];
                    if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                    {
                        edits = value;
                    }

                    text = text[..tilde];
                }

                if (text.EndsWith('*') && text.Length > 1)
                {
                    prefix = true;
                    text = text.TrimEnd('*');
                }

                return new WordNode(text, prefix, edits, fuzzy, boost);
            }

            default:
                throw new QueryParseException($"Operador \"{token.Text}\" fora de lugar.");
        }
    }

    private static void ParseModifiers(string modifiers, out bool hasSlop, out int? slop, out float boost)
    {
        hasSlop = false;
        slop = null;
        boost = 1f;
        var i = 0;
        while (i < modifiers.Length)
        {
            var kind = modifiers[i++];
            var start = i;
            while (i < modifiers.Length && (char.IsAsciiDigit(modifiers[i]) || modifiers[i] == '.'))
            {
                i++;
            }

            var value = modifiers[start..i];
            if (kind == '~')
            {
                hasSlop = true;
                slop = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 2;
            }
            else if (kind == '^')
            {
                boost = ParseFloat(value, 1f);
            }
        }
    }

    private static float ParseFloat(string value, float fallback)
        => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : fallback;

    private Query? Build(Node node, int? field, DefaultOperator defaultOperator)
    {
        switch (node)
        {
            case FieldNode fieldNode:
                return Build(fieldNode.Inner, fieldNode.Field, defaultOperator);
            case GroupNode group:
            {
                var inner = Build(group.Inner, field, defaultOperator);
                if (inner is null || Math.Abs(group.Boost - 1f) < 1e-6)
                {
                    return inner;
                }

                return new BooleanQuery([(inner, Occur.Must)], group.Boost);
            }

            case BoolNode boolean:
            {
                var clauses = new List<(Query, Occur)>();
                foreach (var (child, explicitOccur) in boolean.Clauses)
                {
                    var query = Build(child, field, defaultOperator);
                    if (query is null)
                    {
                        continue;
                    }

                    var occur = explicitOccur
                        ?? (boolean.Disjunction ? Occur.Should
                            : boolean.ExplicitAnd || defaultOperator == DefaultOperator.And ? Occur.Must : Occur.Should);
                    if (occur == Occur.Must && IsKeywordClause(child, field))
                    {
                        occur = Occur.Filter;
                    }

                    clauses.Add((query, occur));
                }

                return clauses.Count == 0 ? null : new BooleanQuery(clauses);
            }

            case WordNode word:
                return field is { } f ? BuildWord(word, f) : MultiField(fieldNumber => BuildWord(word, fieldNumber));
            case PhraseNode phrase:
                return field is { } pf ? BuildPhrase(phrase, pf) : MultiField(fieldNumber => BuildPhrase(phrase, fieldNumber));
            default:
                return null;
        }
    }

    private static bool IsKeywordClause(Node node, int? field)
    {
        return node switch
        {
            FieldNode fieldNode => Schema.IsKeyword(fieldNode.Field) && fieldNode.Inner is WordNode or PhraseNode,
            WordNode or PhraseNode => field is { } f && Schema.IsKeyword(f),
            _ => false,
        };
    }

    private Query? MultiField(Func<int, Query?> factory)
    {
        var clauses = new List<(Query, Occur)>();
        foreach (var (field, boost) in _defaultFields)
        {
            var query = factory(field);
            if (query is null)
            {
                continue;
            }

            clauses.Add((WithBoost(query, boost), Occur.Should));
        }

        return clauses.Count switch
        {
            0 => null,
            1 => clauses[0].Item1,
            _ => new BooleanQuery(clauses),
        };
    }

    private static Query WithBoost(Query query, float boost)
    {
        if (Math.Abs(boost - 1f) < 1e-6)
        {
            return query;
        }

        return query switch
        {
            TermQuery term => new TermQuery(term.Field, term.Variants, term.Boost * boost),
            PhraseQuery phrase => new PhraseQuery(phrase.Field, phrase.Parts, phrase.Slop, phrase.Boost * boost),
            PrefixQuery prefix => new PrefixQuery(prefix.Field, prefix.Prefix, prefix.Boost * boost),
            FuzzyQuery fuzzy => new FuzzyQuery(fuzzy.Field, fuzzy.Term, fuzzy.MaxEdits, fuzzy.Boost * boost),
            BooleanQuery boolean => new BooleanQuery(boolean.Clauses, boolean.Boost * boost),
            _ => new BooleanQuery([(query, Occur.Must)], boost),
        };
    }

    private Query? BuildWord(WordNode word, int field)
    {
        if (Schema.IsKeyword(field))
        {
            var value = KeywordValue(field, word.Text);
            return value.Length == 0 ? null : new TermQuery(field, [value], word.Boost);
        }

        if (word.Prefix)
        {
            var prefix = CharFolding.Fold(word.Text.ToLowerInvariant());
            var tokens = Tokenizer.Tokenize(prefix);
            if (tokens.Count == 0)
            {
                return null;
            }

            var last = tokens[^1];
            return new PrefixQuery(field, prefix.Substring(last.Start, last.Length), word.Boost);
        }

        if (field == Schema.Path)
        {
            var plain = new List<AnalyzedToken>();
            _analyzer.AnalyzePlain(word.Text, plain);
            if (plain.Count == 0)
            {
                return null;
            }

            return plain.Count == 1
                ? new TermQuery(field, [plain[0].Term], word.Boost)
                : new PhraseQuery(field, plain.Select(t => (t.Position, (IReadOnlyList<string>)[t.Term])).ToList(), 0, word.Boost);
        }

        var analyzed = _analyzer.AnalyzeQuery(word.Text);
        if (analyzed.Count == 0)
        {
            return null;
        }

        if (word.Fuzzy && analyzed.Count == 1)
        {
            var term = analyzed[0];
            var edits = word.FuzzyEdits ?? (term.Original.Length >= 6 ? 2 : 1);
            if (term.Variants.Count == 1)
            {
                return new FuzzyQuery(field, term.Variants[0], edits, word.Boost);
            }

            var clauses = term.Variants
                .Select(v => ((Query)new FuzzyQuery(field, v, edits, 1f), Occur.Should))
                .ToList();
            return new BooleanQuery(clauses, word.Boost);
        }

        if (analyzed.Count == 1)
        {
            return new TermQuery(field, analyzed[0].Variants, word.Boost);
        }

        return new PhraseQuery(field, analyzed.Select(t => (t.Position, t.Variants)).ToList(), 0, word.Boost);
    }

    private Query? BuildPhrase(PhraseNode phrase, int field)
    {
        if (Schema.IsKeyword(field))
        {
            var value = KeywordValue(field, phrase.Text);
            return value.Length == 0 ? null : new TermQuery(field, [value], phrase.Boost);
        }

        List<(int, IReadOnlyList<string>)> parts;
        if (field == Schema.Path)
        {
            var plain = new List<AnalyzedToken>();
            _analyzer.AnalyzePlain(phrase.Text, plain);
            parts = plain.Select(t => (t.Position, (IReadOnlyList<string>)[t.Term])).ToList();
        }
        else
        {
            parts = _analyzer.AnalyzeQuery(phrase.Text).Select(t => (t.Position, t.Variants)).ToList();
        }

        if (parts.Count == 0)
        {
            return null;
        }

        var baseOffset = parts[0].Item1;
        var normalized = parts.Select(p => (p.Item1 - baseOffset, p.Item2)).ToList();
        return new PhraseQuery(field, normalized, phrase.Slop, phrase.Boost);
    }

    private static string KeywordValue(int field, string raw)
    {
        var text = raw.Trim();
        return field switch
        {
            Schema.Extension => DocumentBuilder.NormalizeExtension(text),
            Schema.Language => LanguageCodes.FromCode(text) is var language and not Language.Unknown ? language.ToCode() : CharFolding.Fold(text.ToLowerInvariant()),
            Schema.Source => TextAnalyzer.NormalizeKeyword(text),
            _ => text,
        };
    }
}
