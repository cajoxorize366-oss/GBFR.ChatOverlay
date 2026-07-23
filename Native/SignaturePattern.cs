using System.Globalization;

namespace GBFR.ChatOverlay.Native;

public sealed class SignaturePattern
{
    private readonly byte[] _bytes;
    private readonly bool[] _wildcards;
    private readonly int _anchorIndex;

    private SignaturePattern(byte[] bytes, bool[] wildcards)
    {
        _bytes = bytes;
        _wildcards = wildcards;
        _anchorIndex = Array.FindIndex(_wildcards, wildcard => !wildcard);
    }

    public int Length => _bytes.Length;

    public static SignaturePattern Parse(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            throw new FormatException("A signature must contain at least one token.");

        var bytes = new byte[tokens.Length];
        var wildcards = new bool[tokens.Length];
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token is "?" or "??")
            {
                wildcards[index] = true;
                continue;
            }

            if (token.Length != 2 ||
                !byte.TryParse(token, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[index]))
            {
                throw new FormatException($"Invalid signature token '{token}'.");
            }
        }

        if (wildcards.All(value => value))
            throw new FormatException("A signature cannot consist entirely of wildcards.");

        return new SignaturePattern(bytes, wildcards);
    }

    public IReadOnlyList<int> FindOffsets(ReadOnlySpan<byte> source, int maximumMatches = int.MaxValue)
    {
        if (maximumMatches <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumMatches));

        var matches = new List<int>();
        if (source.Length < Length)
            return matches;

        var searchStart = 0;
        var lastCandidate = source.Length - Length;
        while (searchStart <= lastCandidate)
        {
            var anchorSearch = source.Slice(searchStart + _anchorIndex, lastCandidate - searchStart + 1);
            var relativeAnchor = anchorSearch.IndexOf(_bytes[_anchorIndex]);
            if (relativeAnchor < 0)
                break;

            var candidate = searchStart + relativeAnchor;
            if (IsMatch(source.Slice(candidate, Length)))
            {
                matches.Add(candidate);
                if (matches.Count >= maximumMatches)
                    break;
            }

            searchStart = candidate + 1;
        }

        return matches;
    }

    public int FindUniqueOffset(ReadOnlySpan<byte> source, string label)
    {
        var matches = FindOffsets(source, 2);
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"Signature '{label}' was not found."),
            _ => throw new InvalidOperationException($"Signature '{label}' is ambiguous."),
        };
    }

    private bool IsMatch(ReadOnlySpan<byte> candidate)
    {
        for (var index = 0; index < _bytes.Length; index++)
        {
            if (!_wildcards[index] && candidate[index] != _bytes[index])
                return false;
        }

        return true;
    }
}

