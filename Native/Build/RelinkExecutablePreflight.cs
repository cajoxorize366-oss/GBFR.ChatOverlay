using System.Buffers.Binary;
using System.Reflection.PortableExecutable;

namespace GBFR.ChatOverlay.Native;

internal sealed class RelinkExecutablePreflight : IDisposable
{
    private readonly FileStream _stream;
    private readonly PEReader _reader;
    private readonly SectionHeader _textSection;

    private RelinkExecutablePreflight(FileStream stream, PEReader reader, SectionHeader textSection)
    {
        _stream = stream;
        _reader = reader;
        _textSection = textSection;
    }

    internal static RelinkExecutablePreflight Open(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        var stream = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.RandomAccess);
        try
        {
            var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            try
            {
                var textSection = reader.PEHeaders.SectionHeaders
                    .SingleOrDefault(section => string.Equals(section.Name, ".text", StringComparison.Ordinal));
                if (textSection.Name is null)
                    throw new InvalidDataException("The Relink executable has no .text section.");
                return new RelinkExecutablePreflight(stream, reader, textSection);
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal void RequirePattern(int rva, SignaturePattern pattern, string label)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var bytes = ReadBytes(rva, pattern.Length);
        if (!pattern.Matches(bytes))
        {
            throw new InvalidDataException(
                $"Relink required-byte/RVA preflight failed for {label} at RVA 0x{rva:X8}.");
        }
    }

    internal int ReadInt32(int rva) =>
        BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(rva, sizeof(int)));

    internal byte[] ReadBytes(int rva, int count)
    {
        if (rva < 0)
            throw new ArgumentOutOfRangeException(nameof(rva));
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        var relativeOffset = checked(rva - _textSection.VirtualAddress);
        if (relativeOffset < 0 || relativeOffset > _textSection.SizeOfRawData - count)
        {
            throw new InvalidDataException(
                $"Relink RVA range 0x{rva:X8}+0x{count:X} is outside the executable .text section.");
        }

        var bytes = new byte[count];
        _stream.Position = checked(_textSection.PointerToRawData + relativeOffset);
        _stream.ReadExactly(bytes);
        return bytes;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}
