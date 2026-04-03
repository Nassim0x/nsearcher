using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace NSearcher.Core;

internal sealed class SearchPattern
{
    private const int MinimumRegexPrefilterLength = 2;
    private readonly string? _literal;
    private readonly int _literalLength;
    private readonly byte[]? _literalUtf8Bytes;
    private readonly StringComparison _comparison;
    private readonly Regex? _regex;
    private readonly string? _regexPrefilterLiteral;
    private readonly FixedPatternMatcher? _simpleRegexMatcher;
    private readonly byte[]? _prefilterUtf8Bytes;

    private SearchPattern(string literal, StringComparison comparison)
    {
        _literal = literal;
        _literalLength = literal.Length;
        _literalUtf8Bytes = comparison == StringComparison.Ordinal ? Encoding.UTF8.GetBytes(literal) : null;
        _comparison = comparison;
        _prefilterUtf8Bytes = _literalUtf8Bytes;
    }

    private SearchPattern(Regex regex, string? regexPrefilterLiteral, FixedPatternMatcher? simpleRegexMatcher)
    {
        _regex = regex;
        _regexPrefilterLiteral = regexPrefilterLiteral;
        _simpleRegexMatcher = simpleRegexMatcher;
        _comparison = regex.Options.HasFlag(RegexOptions.IgnoreCase)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        _prefilterUtf8Bytes = _comparison == StringComparison.Ordinal && regexPrefilterLiteral is not null
            ? Encoding.UTF8.GetBytes(regexPrefilterLiteral)
            : null;
    }

    public static SearchPattern Create(SearchOptions options)
    {
        var ignoreCase = options.CaseMode switch
        {
            CaseMode.Insensitive => true,
            CaseMode.Sensitive => false,
            _ => ShouldIgnoreCaseInSmartMode(options.Pattern)
        };

        if (options.UseRegex)
        {
            var regexOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant;

            if (ignoreCase)
            {
                regexOptions |= RegexOptions.IgnoreCase;
            }

            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return new SearchPattern(
                new Regex(options.Pattern, regexOptions),
                ExtractRegexPrefilterLiteral(options.Pattern),
                FixedPatternMatcher.TryCreate(options.Pattern, comparison));
        }

        return new SearchPattern(
            options.Pattern,
            ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public SearchLineMatch ScanLine(
        string line,
        bool captureOccurrences,
        bool trackFirstMatchMetadata,
        bool stopAfterFirstMatch)
    {
        if (_regex is not null)
        {
            if (_simpleRegexMatcher is not null)
            {
                return _simpleRegexMatcher.ScanLine(
                    line,
                    captureOccurrences,
                    trackFirstMatchMetadata,
                    stopAfterFirstMatch);
            }

            if (ShouldSkipRegex(line))
            {
                return new SearchLineMatch(0, -1, 0, null);
            }

            List<MatchOccurrence>? matches = null;
            var matchCount = 0;
            var firstMatchStart = -1;
            var firstMatchLength = 0;

            foreach (var match in _regex.EnumerateMatches(line))
            {
                if (match.Length == 0)
                {
                    continue;
                }

                if (trackFirstMatchMetadata && matchCount == 0)
                {
                    firstMatchStart = match.Index;
                    firstMatchLength = match.Length;
                }

                matchCount++;

                if (captureOccurrences)
                {
                    matches ??= [];
                    matches.Add(new MatchOccurrence(match.Index, match.Length));
                }

                if (stopAfterFirstMatch)
                {
                    break;
                }
            }

            return new SearchLineMatch(
                matchCount,
                firstMatchStart,
                firstMatchLength,
                captureOccurrences ? matches : null);
        }

        ArgumentNullException.ThrowIfNull(_literal);

        List<MatchOccurrence>? occurrences = null;
        var needle = _literal.AsSpan();
        var cursor = 0;
        var matchCountLiteral = 0;
        var firstLiteralMatchStart = -1;

        while (cursor <= line.Length - _literalLength)
        {
            var index = line.AsSpan(cursor).IndexOf(needle, _comparison);
            if (index < 0)
            {
                break;
            }

            var matchStart = cursor + index;
            if (trackFirstMatchMetadata && matchCountLiteral == 0)
            {
                firstLiteralMatchStart = matchStart;
            }

            matchCountLiteral++;

            if (captureOccurrences)
            {
                occurrences ??= [];
                occurrences.Add(new MatchOccurrence(matchStart, _literalLength));
            }

            if (stopAfterFirstMatch)
            {
                break;
            }

            cursor = matchStart + _literalLength;
        }

        return new SearchLineMatch(
            matchCountLiteral,
            firstLiteralMatchStart,
            trackFirstMatchMetadata && matchCountLiteral > 0 ? _literalLength : 0,
            captureOccurrences ? occurrences : null);
    }

    public int CountLineMatches(ReadOnlySpan<char> line, bool stopAfterFirstMatch)
    {
        if (_regex is not null)
        {
            if (_simpleRegexMatcher is not null)
            {
                return _simpleRegexMatcher.CountLineMatches(line, stopAfterFirstMatch);
            }

            if (ShouldSkipRegex(line))
            {
                return 0;
            }

            var count = 0;

            foreach (var match in _regex.EnumerateMatches(line))
            {
                if (match.Length == 0)
                {
                    continue;
                }

                count++;

                if (stopAfterFirstMatch)
                {
                    return 1;
                }
            }

            return count;
        }

        ArgumentNullException.ThrowIfNull(_literal);

        var needle = _literal.AsSpan();
        var cursor = 0;
        var matchCount = 0;

        while (cursor <= line.Length - _literalLength)
        {
            var index = line[cursor..].IndexOf(needle, _comparison);
            if (index < 0)
            {
                break;
            }

            matchCount++;
            if (stopAfterFirstMatch)
            {
                return 1;
            }

            cursor += index + _literalLength;
        }

        return matchCount;
    }

    public bool TryGetLiteralUtf8Bytes([NotNullWhen(true)] out byte[]? utf8Bytes)
    {
        utf8Bytes = _literalUtf8Bytes;
        return utf8Bytes is not null;
    }

    public bool TryGetPrefilterUtf8Bytes([NotNullWhen(true)] out byte[]? utf8Bytes)
    {
        utf8Bytes = _prefilterUtf8Bytes;
        return utf8Bytes is not null;
    }

    private bool ShouldSkipRegex(ReadOnlySpan<char> line) =>
        _regexPrefilterLiteral is not null &&
        line.IndexOf(_regexPrefilterLiteral.AsSpan(), _comparison) < 0;

    private static string? ExtractRegexPrefilterLiteral(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return null;
        }

        var current = new StringBuilder(capacity: 16);
        string? best = null;
        var escaped = false;
        var inCharacterClass = false;

        static string? PickLongerLiteral(string? currentBest, string candidate)
        {
            return currentBest is null || candidate.Length > currentBest.Length
                ? candidate
                : currentBest;
        }

        foreach (var character in pattern)
        {
            if (escaped)
            {
                escaped = false;

                if (TryTranslateEscapedLiteral(character, out var escapedLiteral))
                {
                    current.Append(escapedLiteral);
                    continue;
                }

                if (current.Length >= MinimumRegexPrefilterLength)
                {
                    best = PickLongerLiteral(best, current.ToString());
                }

                current.Clear();
                continue;
            }

            if (inCharacterClass)
            {
                if (character == ']')
                {
                    inCharacterClass = false;
                }

                if (current.Length >= MinimumRegexPrefilterLength)
                {
                    best = PickLongerLiteral(best, current.ToString());
                }

                current.Clear();
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '[')
            {
                if (current.Length >= MinimumRegexPrefilterLength)
                {
                    best = PickLongerLiteral(best, current.ToString());
                }

                current.Clear();
                inCharacterClass = true;
                continue;
            }

            if (IsRegexMetaCharacter(character))
            {
                if (current.Length >= MinimumRegexPrefilterLength)
                {
                    best = PickLongerLiteral(best, current.ToString());
                }

                current.Clear();
                continue;
            }

            current.Append(character);
        }

        if (current.Length >= MinimumRegexPrefilterLength)
        {
            best = PickLongerLiteral(best, current.ToString());
        }

        return best;
    }

    private static bool TryTranslateEscapedLiteral(char escaped, out char literal)
    {
        switch (escaped)
        {
            case '\\':
            case '.':
            case '+':
            case '*':
            case '?':
            case '^':
            case '$':
            case '(':
            case ')':
            case '[':
            case ']':
            case '{':
            case '}':
            case '|':
            case '-':
            case '/':
                literal = escaped;
                return true;
            default:
                literal = default;
                return false;
        }
    }

    private static bool IsRegexMetaCharacter(char character) =>
        character is '.' or '^' or '$' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '|';

    private static bool ShouldIgnoreCaseInSmartMode(string pattern)
    {
        foreach (var character in pattern)
        {
            if (char.IsUpper(character))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class FixedPatternMatcher
    {
        private readonly PatternOp[] _ops;
        private readonly int _patternLength;
        private readonly StringComparison _comparison;
        private readonly string? _scanLiteral;
        private readonly int _scanLiteralOffset;

        private FixedPatternMatcher(
            PatternOp[] ops,
            int patternLength,
            StringComparison comparison,
            string? scanLiteral,
            int scanLiteralOffset)
        {
            _ops = ops;
            _patternLength = patternLength;
            _comparison = comparison;
            _scanLiteral = scanLiteral;
            _scanLiteralOffset = scanLiteralOffset;
        }

        public static FixedPatternMatcher? TryCreate(string pattern, StringComparison comparison)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return null;
            }

            var ops = new List<PatternOp>(capacity: 8);
            var literalBuilder = new StringBuilder(capacity: pattern.Length);
            string? bestLiteral = null;
            var bestLiteralOffset = 0;
            var currentLength = 0;
            var sawRegexConstruct = false;

            void FlushLiteral()
            {
                if (literalBuilder.Length == 0)
                {
                    return;
                }

                var literal = literalBuilder.ToString();
                var literalOffset = currentLength - literal.Length;
                ops.Add(PatternOp.CreateLiteral(literal));

                if (bestLiteral is null || literal.Length > bestLiteral.Length)
                {
                    bestLiteral = literal;
                    bestLiteralOffset = literalOffset;
                }

                literalBuilder.Clear();
            }

            for (var index = 0; index < pattern.Length; index++)
            {
                var character = pattern[index];

                if (character == '\\')
                {
                    if (++index >= pattern.Length)
                    {
                        return null;
                    }

                    character = pattern[index];

                    if (TryMapEscapedCharClass(character, out var charClass))
                    {
                        FlushLiteral();
                        if (!TryReadFixedQuantifier(pattern, ref index, out var repeatCount))
                        {
                            return null;
                        }

                        ops.Add(PatternOp.CreateCharClass(charClass, repeatCount));
                        currentLength += repeatCount;
                        sawRegexConstruct = true;
                        continue;
                    }

                    if (!TryTranslateEscapedLiteral(character, out var escapedLiteral))
                    {
                        return null;
                    }

                    if (!TryReadFixedQuantifier(pattern, ref index, out var escapedRepeatCount))
                    {
                        return null;
                    }

                    literalBuilder.Append(escapedLiteral, escapedRepeatCount);
                    currentLength += escapedRepeatCount;
                    continue;
                }

                if (character == '.')
                {
                    FlushLiteral();
                    if (!TryReadFixedQuantifier(pattern, ref index, out var repeatCount))
                    {
                        return null;
                    }

                    ops.Add(PatternOp.CreateCharClass(SimpleRegexCharClass.Any, repeatCount));
                    currentLength += repeatCount;
                    sawRegexConstruct = true;
                    continue;
                }

                if (IsRegexMetaCharacter(character))
                {
                    return null;
                }

                if (!TryReadFixedQuantifier(pattern, ref index, out var literalRepeatCount))
                {
                    return null;
                }

                if (literalRepeatCount > 1)
                {
                    sawRegexConstruct = true;
                }

                literalBuilder.Append(character, literalRepeatCount);
                currentLength += literalRepeatCount;
            }

            FlushLiteral();

            if (!sawRegexConstruct || ops.Count == 0 || currentLength == 0)
            {
                return null;
            }

            return new FixedPatternMatcher(
                ops.ToArray(),
                currentLength,
                comparison,
                bestLiteral,
                bestLiteralOffset);
        }

        public SearchLineMatch ScanLine(
            ReadOnlySpan<char> line,
            bool captureOccurrences,
            bool trackFirstMatchMetadata,
            bool stopAfterFirstMatch)
        {
            List<MatchOccurrence>? occurrences = null;
            var matchCount = 0;
            var firstMatchStart = -1;
            var firstMatchLength = 0;
            var searchStart = 0;

            while (TryFindNextMatch(line, ref searchStart, out var matchStart))
            {
                if (trackFirstMatchMetadata && matchCount == 0)
                {
                    firstMatchStart = matchStart;
                    firstMatchLength = _patternLength;
                }

                matchCount++;

                if (captureOccurrences)
                {
                    occurrences ??= [];
                    occurrences.Add(new MatchOccurrence(matchStart, _patternLength));
                }

                if (stopAfterFirstMatch)
                {
                    break;
                }
            }

            return new SearchLineMatch(
                matchCount,
                firstMatchStart,
                firstMatchLength,
                captureOccurrences ? occurrences : null);
        }

        public int CountLineMatches(ReadOnlySpan<char> line, bool stopAfterFirstMatch)
        {
            var matchCount = 0;
            var searchStart = 0;

            while (TryFindNextMatch(line, ref searchStart, out _))
            {
                matchCount++;

                if (stopAfterFirstMatch)
                {
                    return 1;
                }
            }

            return matchCount;
        }

        private bool TryFindNextMatch(ReadOnlySpan<char> line, ref int searchStart, out int matchStart)
        {
            while (searchStart <= line.Length - _patternLength)
            {
                var candidateStart = FindNextCandidate(line, searchStart);
                if (candidateStart < 0)
                {
                    break;
                }

                if (IsMatchAt(line, candidateStart))
                {
                    matchStart = candidateStart;
                    searchStart = candidateStart + _patternLength;
                    return true;
                }

                searchStart = candidateStart + 1;
            }

            matchStart = -1;
            return false;
        }

        private int FindNextCandidate(ReadOnlySpan<char> line, int searchStart)
        {
            if (_scanLiteral is null)
            {
                return searchStart;
            }

            var literalStartSearch = searchStart + _scanLiteralOffset;
            if (literalStartSearch > line.Length - _scanLiteral.Length)
            {
                return -1;
            }

            var literalIndex = line[literalStartSearch..].IndexOf(_scanLiteral.AsSpan(), _comparison);
            if (literalIndex < 0)
            {
                return -1;
            }

            var candidateStart = literalStartSearch + literalIndex - _scanLiteralOffset;
            return candidateStart <= line.Length - _patternLength
                ? candidateStart
                : -1;
        }

        private bool IsMatchAt(ReadOnlySpan<char> line, int start)
        {
            var cursor = start;

            foreach (var op in _ops)
            {
                if (op.Literal is not null)
                {
                    if (!line.Slice(cursor, op.Length).Equals(op.Literal.AsSpan(), _comparison))
                    {
                        return false;
                    }

                    cursor += op.Length;
                    continue;
                }

                for (var index = 0; index < op.Length; index++)
                {
                    if (!MatchesCharClass(line[cursor + index], op.CharClass))
                    {
                        return false;
                    }
                }

                cursor += op.Length;
            }

            return true;
        }

        private static bool TryReadFixedQuantifier(string pattern, ref int index, out int repeatCount)
        {
            repeatCount = 1;

            if (index + 1 >= pattern.Length)
            {
                return true;
            }

            var next = pattern[index + 1];
            if (next is '*' or '+' or '?')
            {
                return false;
            }

            if (next != '{')
            {
                return true;
            }

            var cursor = index + 2;
            if (cursor >= pattern.Length || !char.IsAsciiDigit(pattern[cursor]))
            {
                return false;
            }

            var value = 0;
            while (cursor < pattern.Length && char.IsAsciiDigit(pattern[cursor]))
            {
                value = checked((value * 10) + (pattern[cursor] - '0'));
                cursor++;
            }

            if (cursor >= pattern.Length || pattern[cursor] != '}' || value <= 0)
            {
                return false;
            }

            repeatCount = value;
            index = cursor;
            return true;
        }

        private static bool TryMapEscapedCharClass(char escaped, out SimpleRegexCharClass charClass)
        {
            switch (escaped)
            {
                case 'd':
                    charClass = SimpleRegexCharClass.Digit;
                    return true;
                case 'D':
                    charClass = SimpleRegexCharClass.NotDigit;
                    return true;
                case 'w':
                    charClass = SimpleRegexCharClass.Word;
                    return true;
                case 'W':
                    charClass = SimpleRegexCharClass.NotWord;
                    return true;
                case 's':
                    charClass = SimpleRegexCharClass.Whitespace;
                    return true;
                case 'S':
                    charClass = SimpleRegexCharClass.NotWhitespace;
                    return true;
                default:
                    charClass = default;
                    return false;
            }
        }

        private static bool MatchesCharClass(char character, SimpleRegexCharClass charClass)
        {
            return charClass switch
            {
                SimpleRegexCharClass.Any => true,
                SimpleRegexCharClass.Digit => char.IsAsciiDigit(character),
                SimpleRegexCharClass.NotDigit => !char.IsAsciiDigit(character),
                SimpleRegexCharClass.Word => char.IsLetterOrDigit(character) || character == '_',
                SimpleRegexCharClass.NotWord => !(char.IsLetterOrDigit(character) || character == '_'),
                SimpleRegexCharClass.Whitespace => char.IsWhiteSpace(character),
                SimpleRegexCharClass.NotWhitespace => !char.IsWhiteSpace(character),
                _ => false
            };
        }

        private readonly record struct PatternOp(string? Literal, SimpleRegexCharClass CharClass, int Length)
        {
            public static PatternOp CreateLiteral(string literal) => new(literal, default, literal.Length);

            public static PatternOp CreateCharClass(SimpleRegexCharClass charClass, int length) => new(null, charClass, length);
        }

        private enum SimpleRegexCharClass : byte
        {
            Any,
            Digit,
            NotDigit,
            Word,
            NotWord,
            Whitespace,
            NotWhitespace
        }
    }
}
