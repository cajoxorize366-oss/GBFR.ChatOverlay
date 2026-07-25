using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;

namespace GBFR.ChatOverlay.Overlay;

internal sealed class ImeCandidateSnapshot
{
    private const int MaximumVisibleCandidates = 10;

    private readonly ReadOnlyCollection<string> _candidates;

    internal ImeCandidateSnapshot(
        uint listIndex,
        uint style,
        uint selection,
        uint pageStart,
        uint pageSize,
        IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        ListIndex = listIndex;
        Style = style;
        _candidates = Array.AsReadOnly(candidates.ToArray());
        SelectedIndex = selection < _candidates.Count ? checked((int)selection) : -1;
        PageStart = pageStart < _candidates.Count ? checked((int)pageStart) : 0;

        var remaining = _candidates.Count - PageStart;
        var requestedPageSize = pageSize == 0
            ? Math.Min(9, remaining)
            : checked((int)Math.Min(pageSize, int.MaxValue));
        PageSize = Math.Clamp(requestedPageSize, 0, Math.Min(remaining, MaximumVisibleCandidates));
    }

    internal uint ListIndex { get; }

    internal uint Style { get; }

    internal IReadOnlyList<string> Candidates => _candidates;

    internal int Count => _candidates.Count;

    internal int SelectedIndex { get; }

    internal int PageStart { get; }

    internal int PageSize { get; }

    internal string BuildDisplayText()
    {
        if (Count == 0 || PageSize == 0)
            return string.Empty;

        var builder = new StringBuilder("候选：");
        var pageEnd = Math.Min(Count, PageStart + PageSize);
        for (var index = PageStart; index < pageEnd; index++)
        {
            if (index > PageStart)
                builder.Append("   ");

            var pageIndex = index - PageStart + 1;
            var keyLabel = pageIndex == 10 ? 0 : pageIndex;
            var selected = index == SelectedIndex;
            if (selected)
                builder.Append('[');
            builder.Append(keyLabel).Append('.').Append(_candidates[index]);
            if (selected)
                builder.Append(']');
        }

        return builder.ToString();
    }
}

internal static class ImeCandidateListParser
{
    private const int FixedHeaderSize = 24;
    private const uint MaximumCandidateCount = 1_024;

    internal static bool TryParse(
        ReadOnlySpan<byte> buffer,
        uint listIndex,
        out ImeCandidateSnapshot? snapshot)
    {
        snapshot = null;
        if (buffer.Length < FixedHeaderSize)
            return false;

        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var style = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]);
        var selection = BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..]);
        var pageStart = BinaryPrimitives.ReadUInt32LittleEndian(buffer[16..]);
        var pageSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer[20..]);
        if (declaredSize < FixedHeaderSize ||
            declaredSize > buffer.Length ||
            count > MaximumCandidateCount)
        {
            return false;
        }

        var offsetTableEnd = checked(FixedHeaderSize + (int)count * sizeof(uint));
        if (offsetTableEnd > declaredSize)
            return false;

        var declaredBuffer = buffer[..checked((int)declaredSize)];
        var candidates = new string[count];
        for (var index = 0; index < count; index++)
        {
            var offsetPosition = FixedHeaderSize + index * sizeof(uint);
            var stringOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                declaredBuffer.Slice(offsetPosition, sizeof(uint)));
            if (stringOffset < offsetTableEnd ||
                stringOffset >= declaredSize ||
                (stringOffset & 1) != 0)
            {
                return false;
            }

            var encodedText = declaredBuffer[checked((int)stringOffset)..];
            var terminator = FindUtf16Terminator(encodedText);
            if (terminator < 0)
                return false;

            candidates[index] = Encoding.Unicode.GetString(encodedText[..terminator]);
        }

        snapshot = new ImeCandidateSnapshot(
            listIndex,
            style,
            selection,
            pageStart,
            pageSize,
            candidates);
        return true;
    }

    private static int FindUtf16Terminator(ReadOnlySpan<byte> encodedText)
    {
        for (var index = 0; index + 1 < encodedText.Length; index += 2)
        {
            if (encodedText[index] == 0 && encodedText[index + 1] == 0)
                return index;
        }

        return -1;
    }
}
