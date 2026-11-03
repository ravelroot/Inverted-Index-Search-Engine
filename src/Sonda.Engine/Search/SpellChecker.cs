using System.Text;
using Sonda.Analysis;
using Sonda.Storage.Formats;

namespace Sonda.Engine.Search;

public sealed record SpellingCorrection(string Original, string Suggestion, int Start, int Length, int Distance, long DocFreq);

public sealed record SpellingSuggestion(string Query, string Corrected, long Total, IReadOnlyList<SpellingCorrection> Corrections);

internal sealed class SpellChecker(TextAnalyzer analyzer)
{
    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal) { "OR", "OU", "AND", "E", "NOT", "NAO", "NÃO" };

    public IReadOnlyList<SpellingCorrection> Check(IndexSearcher searcher, string query, int maxCorrections = 5)
    {
        var corrections = new List<SpellingCorrection>();
        if (string.IsNullOrWhiteSpace(query) || query.Length > 500)
        {
            return corrections;
        }

        foreach (var span in Tokenizer.Tokenize(query))
        {
            if (corrections.Count >= maxCorrections)
            {
                break;
            }

            var word = query.Substring(span.Start, span.Length);
            if (!Eligible(query, span.Start, span.Length, word))
            {
                continue;
            }

            var variants = analyzer.QueryVariants(word);
            if (variants.Count == 0 || variants.Any(v => Known(searcher, v)))
            {
                continue;
            }

            var folded = CharFolding.Fold(word.ToLowerInvariant());
            var maxEdits = folded.Length >= 6 ? 2 : 1;
            var best = BestTerm(searcher, variants, maxEdits);
            if (best is null)
            {
                continue;
            }

            var surface = SurfaceForm(searcher, best.Value.Term, folded);
            if (surface is null || string.Equals(CharFolding.Fold(surface), folded, StringComparison.Ordinal))
            {
                continue;
            }

            corrections.Add(new SpellingCorrection(word, MatchCase(word, surface), span.Start, span.Length, EditDistance.Compute(folded, CharFolding.Fold(surface), 3), best.Value.DocFreq));
        }

        return corrections;
    }

    public static string Apply(string query, IReadOnlyList<SpellingCorrection> corrections)
    {
        var builder = new StringBuilder(query.Length + 16);
        var position = 0;
        foreach (var correction in corrections.OrderBy(c => c.Start))
        {
            builder.Append(query, position, correction.Start - position);
            builder.Append(correction.Suggestion);
            position = correction.Start + correction.Length;
        }

        builder.Append(query, position, query.Length - position);
        return builder.ToString();
    }

    private static bool Eligible(string query, int start, int length, string word)
    {
        if (word.Length < 3 || Operators.Contains(word) || word.Any(char.IsDigit))
        {
            return false;
        }

        var end = start + length;
        if (end < query.Length && query[end] is ':' or '*' or '~')
        {
            return false;
        }

        if (start > 0 && query[start - 1] == '-' && (start == 1 || char.IsWhiteSpace(query[start - 2]) || query[start - 2] == '('))
        {
            return false;
        }

        if (start > 0 && query[start - 1] == ':')
        {
            var fieldEnd = start - 1;
            var fieldStart = fieldEnd;
            while (fieldStart > 0 && Tokenizer.IsWordChar(query[fieldStart - 1]))
            {
                fieldStart--;
            }

            var field = query[fieldStart..fieldEnd];
            return Schema.TryResolveField(field, out var resolved) && (resolved == Schema.Title || resolved == Schema.Body);
        }

        return true;
    }

    private static bool Known(IndexSearcher searcher, string term)
        => searcher.TermStatistics(Schema.Body, term).DocFreq > 0 || searcher.TermStatistics(Schema.Title, term).DocFreq > 0;

    private static (string Term, int Distance, long DocFreq)? BestTerm(IndexSearcher searcher, IReadOnlyList<string> variants, int maxEdits)
    {
        (string Term, int Distance, long DocFreq)? best = null;
        foreach (var variant in variants.Distinct(StringComparer.Ordinal))
        {
            if (variant.Length == 0)
            {
                continue;
            }

            var candidates = new Dictionary<string, (int Distance, long DocFreq)>(StringComparer.Ordinal);
            var prefixBytes = Encoding.UTF8.GetBytes(variant[..1]);
            foreach (var leaf in searcher.Leaves)
            {
                var terms = leaf.Reader.Terms(Schema.Body);
                if (terms is null)
                {
                    continue;
                }

                var iterator = terms.SeekCeil(prefixBytes);
                while (iterator.MoveNext() && iterator.TermBytes.StartsWith(prefixBytes))
                {
                    var candidate = iterator.Term;
                    if (Math.Abs(candidate.Length - variant.Length) > maxEdits)
                    {
                        continue;
                    }

                    var distance = EditDistance.Compute(variant, candidate, maxEdits);
                    if (distance == 0 || distance > maxEdits)
                    {
                        continue;
                    }

                    var existing = candidates.GetValueOrDefault(candidate);
                    candidates[candidate] = (distance, existing.DocFreq + iterator.Meta.DocFreq);
                }
            }

            foreach (var (term, value) in candidates)
            {
                if (best is null
                    || value.Distance < best.Value.Distance
                    || (value.Distance == best.Value.Distance && value.DocFreq > best.Value.DocFreq)
                    || (value.Distance == best.Value.Distance && value.DocFreq == best.Value.DocFreq && string.CompareOrdinal(term, best.Value.Term) < 0))
                {
                    best = (term, value.Distance, value.DocFreq);
                }
            }
        }

        return best;
    }

    private string? SurfaceForm(IndexSearcher searcher, string term, string typed)
    {
        var info = Schema.Fields[Schema.Body];
        if (info is null)
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(term);
        var forms = new Dictionary<string, int>(StringComparer.Ordinal);
        var inspected = 0;
        foreach (var leaf in searcher.Leaves)
        {
            var terms = leaf.Reader.Terms(Schema.Body);
            if (terms is null || !terms.TryGetTerm(bytes, out var meta))
            {
                continue;
            }

            var enumerator = leaf.Reader.Core.PostingsFile.Postings(meta, info.IndexOptions, false);
            while (inspected < 3 && enumerator.NextDoc() != PostingsEnum.NoMoreDocs)
            {
                var doc = enumerator.DocId;
                if (leaf.Reader.IsDeleted(doc))
                {
                    continue;
                }

                inspected++;
                var stored = IndexSearcher.ToStored(leaf.Reader.Core.Stored.Document(doc));
                var text = $"{stored.Title}\n{stored.Body ?? stored.Description ?? string.Empty}";
                if (text.Length > 100_000)
                {
                    text = text[..100_000];
                }

                var language = LanguageCodes.FromCode(stored.Language);
                foreach (var span in Tokenizer.Tokenize(text))
                {
                    var word = text.AsSpan(span.Start, span.Length);
                    var analyzed = language == Language.Unknown
                        ? analyzer.QueryVariants(word).FirstOrDefault(v => v == term)
                        : analyzer.AnalyzeWord(word, language);
                    if (!string.Equals(analyzed, term, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var lower = word.ToString().ToLowerInvariant();
                    forms[lower] = forms.GetValueOrDefault(lower) + 1;
                }
            }

            if (inspected >= 3)
            {
                break;
            }
        }

        return forms.Count == 0
            ? null
            : forms
                .OrderBy(f => EditDistance.Compute(typed, CharFolding.Fold(f.Key), 8))
                .ThenByDescending(f => f.Value)
                .ThenBy(f => f.Key, StringComparer.Ordinal)
                .First().Key;
    }

    private static string MatchCase(string original, string suggestion)
    {
        if (original.Length > 1 && original.All(c => !char.IsLetter(c) || char.IsUpper(c)))
        {
            return suggestion.ToUpperInvariant();
        }

        return char.IsUpper(original[0]) ? char.ToUpperInvariant(suggestion[0]) + suggestion[1..] : suggestion;
    }
}
