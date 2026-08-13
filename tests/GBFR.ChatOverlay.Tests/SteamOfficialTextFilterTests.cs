using System.Runtime.InteropServices;
using System.Text;
using GBFR.ChatOverlay.Core;
using GBFR.ChatOverlay.Native;

namespace GBFR.ChatOverlay.Tests;

public sealed class SteamOfficialTextFilterTests
{
    private const int Ni = 0x4F60;
    private const int Hao = 0x597D;
    private const int Grass = 0x8349;

    [Fact]
    public void Refresh_InitTrue_ReturnsReadyAndUsesChatContextWithZeroSource()
    {
        var calls = new List<(int Context, ulong Source, int Capacity)>();
        var filter = CreateFilter(new SteamOfficialTextFilterExports(
            _ => true,
            (context, source, input, output, capacity) =>
            {
                calls.Add((context, source, (int)capacity));
                CopyCString(input, output, (int)capacity);
                return 0;
            }));

        var status = filter.Refresh();

        Assert.Equal(OfficialTextFilterState.Ready, status.State);
        Assert.Equal(OfficialTextFilterState.Ready, filter.Status.State);
        var result = filter.Filter("hello");
        Assert.True(result.Succeeded);
        Assert.False(result.Matched);
        Assert.Equal("hello", result.Text);
        var call = Assert.Single(calls);
        Assert.Equal(2, call.Context);
        Assert.Equal(0UL, call.Source);
        Assert.Equal(Encoding.UTF8.GetByteCount("hello") * 3 + 1, call.Capacity);
    }

    [Fact]
    public void Filter_EmptyStringReturnsPassthrough()
    {
        var filter = CreateFilter(new SteamOfficialTextFilterExports(
            _ => true,
            (_, _, input, output, capacity) =>
            {
                Assert.Equal(1, (int)capacity);
                Assert.Equal(string.Empty, ReadCString(input, 1));
                WriteCString(output, string.Empty, 1);
                return 0;
            }));
        filter.Refresh();

        var result = filter.Filter(string.Empty);

        Assert.True(result.Succeeded);
        Assert.False(result.Matched);
        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(0, result.FilteredCharacterCount);
    }

    [Fact]
    public void Refresh_InitFalse_ReturnsPassthroughAndFilterStillCallsNative()
    {
        var filterCalls = 0;
        var filter = CreateFilter(new SteamOfficialTextFilterExports(
            _ => false,
            (_, _, input, output, capacity) =>
            {
                filterCalls++;
                CopyCString(input, output, (int)capacity);
                return 0;
            }));

        var status = filter.Refresh();

        Assert.Equal(OfficialTextFilterState.Passthrough, status.State);
        var result = filter.Filter("hello");
        Assert.True(result.Succeeded);
        Assert.False(result.Matched);
        Assert.Equal("hello", result.Text);
        Assert.Equal(1, filterCalls);
    }

    [Fact]
    public void Filter_HitReturnsMaskedTextAndCount()
    {
        var filter = CreateFilter(new SteamOfficialTextFilterExports(
            _ => true,
            (_, _, input, output, capacity) =>
            {
                Assert.Equal("bad", ReadCString(input, (int)capacity));
                WriteCString(output, "***", (int)capacity);
                return 1;
            }));
        filter.Refresh();

        var result = filter.Filter("bad");

        Assert.True(result.Succeeded);
        Assert.True(result.Matched);
        Assert.Equal("***", result.Text);
        Assert.Equal(1, result.FilteredCharacterCount);
    }

    [Fact]
    public void Filter_ChineseHitReturnsFilteredUnicodeText()
    {
        var input = ChineseText(Ni, Hao, Grass);
        var expected = ChineseText(Ni, Hao) + "*";
        var filter = CreateFilter(new SteamOfficialTextFilterExports(
            _ => true,
            (_, _, inputPtr, output, capacity) =>
            {
                Assert.Equal(input, ReadCString(inputPtr, (int)capacity));
                WriteCString(output, expected, (int)capacity);
                return 1;
            }));
        filter.Refresh();

        var result = filter.Filter(input);

        Assert.True(result.Succeeded);
        Assert.True(result.Matched);
        Assert.Equal(expected, result.Text);
        Assert.Equal(1, result.FilteredCharacterCount);
    }

    [Fact]
    public void Filter_ChineseNoHitReturnsUnchangedText()
    {
        var input = ChineseText(Ni, Hao);
        var filter = CreateFilter(new SteamOfficialTextFilterExports(
            _ => true,
            (_, _, inputPtr, output, capacity) =>
            {
                CopyCString(inputPtr, output, (int)capacity);
                return 0;
            }));
        filter.Refresh();

        var result = filter.Filter(input);

        Assert.True(result.Succeeded);
        Assert.False(result.Matched);
        Assert.Equal(input, result.Text);
        Assert.Equal(0, result.FilteredCharacterCount);
    }

    [Fact]
    public void Filter_AsciiInputCanExpandToSameCountThreeByteUtf8Replacement()
    {
        var expected = ChineseText(Ni, Hao);
        var filter = CreateFilter(new SteamOfficialTextFilterExports(
            _ => true,
            (_, _, input, output, capacity) =>
            {
                Assert.Equal("ab", ReadCString(input, (int)capacity));
                WriteCString(output, expected, (int)capacity);
                return expected.Length;
            }));
        filter.Refresh();

        var result = filter.Filter("ab");

        Assert.True(result.Succeeded);
        Assert.True(result.Matched);
        Assert.Equal(expected, result.Text);
        Assert.Equal(expected.Length, result.FilteredCharacterCount);
    }

    [Fact]
    public void Filter_RepeatedCallsReadExactNulTerminatedInput()
    {
        const string expectedInput = "repeated";
        var expectedInputByteCount = Encoding.UTF8.GetByteCount(expectedInput) + 1;
        var observedInputs = new List<string>();
        var filter = CreateFilter(new SteamOfficialTextFilterExports(
            _ => true,
            (_, _, input, output, capacity) =>
            {
                var inputBytes = new byte[expectedInputByteCount];
                Marshal.Copy(input, inputBytes, 0, expectedInputByteCount);
                var observedInput = new UTF8Encoding(false, true).GetString(
                    inputBytes,
                    0,
                    expectedInputByteCount - 1);
                Assert.Equal(expectedInput, observedInput);
                Assert.Equal((byte)0, inputBytes[expectedInputByteCount - 1]);
                observedInputs.Add(observedInput);
                CopyCString(input, output, (int)capacity);
                return 0;
            }));
        filter.Refresh();

        for (var i = 0; i < 1000; i++)
        {
            var result = filter.Filter(expectedInput);
            Assert.True(result.Succeeded);
            Assert.False(result.Matched);
            Assert.Equal(expectedInput, result.Text);
        }

        Assert.Equal(1000, observedInputs.Count);
        Assert.All(observedInputs, input => Assert.Equal(expectedInput, input));
    }

    [Fact]
    public void Refresh_MissingExports_ReturnsUnavailableAndFilterFailsOpen()
    {
        var filter = new SteamOfficialTextFilter(() => null);

        var status = filter.Refresh();

        Assert.Equal(OfficialTextFilterState.Unavailable, status.State);
        Assert.Equal(OfficialTextFilterState.Unavailable, filter.Status.State);
        var result = filter.Filter("bad");
        Assert.False(result.Succeeded);
        Assert.False(result.Matched);
        Assert.Equal("bad", result.Text);
        Assert.Equal(0, result.FilteredCharacterCount);
    }

    [Fact]
    public void Refresh_ResolverThrows_ReturnsUnavailableAndFilterFailsOpen()
    {
        var filter = new SteamOfficialTextFilter(
            () => throw new InvalidOperationException("missing"));

        var status = filter.Refresh();

        Assert.Equal(OfficialTextFilterState.Unavailable, status.State);
        var result = filter.Filter("bad");
        Assert.False(result.Succeeded);
        Assert.Equal("bad", result.Text);
    }

    [Fact]
    public void Refresh_InitThrows_ReturnsUnavailableAndFilterFailsOpen()
    {
        var filter = CreateFilter(new SteamOfficialTextFilterExports(
            _ => throw new InvalidOperationException("init failed"),
            (_, _, input, output, capacity) =>
            {
                CopyCString(input, output, (int)capacity);
                return 0;
            }));

        var status = filter.Refresh();

        Assert.Equal(OfficialTextFilterState.Unavailable, status.State);
        var result = filter.Filter("bad");
        Assert.False(result.Succeeded);
        Assert.Equal("bad", result.Text);
    }

    [Fact]
    public void Filter_NativeThrows_FailsOpen()
    {
        var filter = CreateFilter(new SteamOfficialTextFilterExports(
            _ => true,
            (_, _, _, _, _) => throw new InvalidOperationException("native failed")));
        filter.Refresh();

        var result = filter.Filter("bad");

        Assert.False(result.Succeeded);
        Assert.False(result.Matched);
        Assert.Equal("bad", result.Text);
        Assert.Equal(0, result.FilteredCharacterCount);
    }

    [Fact]
    public void Filter_InvalidUtf8Output_FailsOpen()
    {
        var invalidBytes = new byte[] { 0xC3, 0x28, 0 };
        var filter = CreateFilter(new SteamOfficialTextFilterExports(
            _ => true,
            (_, _, _, output, capacity) =>
            {
                Assert.True(invalidBytes.Length <= capacity);
                Marshal.Copy(invalidBytes, 0, output, invalidBytes.Length);
                return 0;
            }));
        filter.Refresh();

        var result = filter.Filter("xxxx");

        Assert.False(result.Succeeded);
        Assert.Equal("xxxx", result.Text);
    }

    [Fact]
    public void Filter_MissingNulTerminator_FailsOpen()
    {
        var filter = CreateFilter(new SteamOfficialTextFilterExports(
            _ => true,
            (_, _, _, output, capacity) =>
            {
                for (var i = 0; i < capacity; i++)
                {
                    Marshal.WriteByte(output, i, 0xFF);
                }

                return 0;
            }));
        filter.Refresh();

        var result = filter.Filter("x");

        Assert.False(result.Succeeded);
        Assert.Equal("x", result.Text);
    }

    [Fact]
    public void Filter_EmbeddedNulInput_FailsOpen()
    {
        var inputWithNul = "a" + (char)0 + "b";
        var filter = CreateFilter(PassthroughExports());
        filter.Refresh();

        var result = filter.Filter(inputWithNul);

        Assert.False(result.Succeeded);
        Assert.Equal(inputWithNul, result.Text);
    }

    [Fact]
    public void Filter_InputOverFixedMaximum_FailsOpenWithoutNativeCall()
    {
        var nativeCalls = 0;
        var filter = CreateFilter(new SteamOfficialTextFilterExports(
            _ => true,
            (_, _, input, output, capacity) =>
            {
                nativeCalls++;
                CopyCString(input, output, (int)capacity);
                return 0;
            }));
        filter.Refresh();

        var oversized = new string('a', 2048 + 1);
        var result = filter.Filter(oversized);

        Assert.False(result.Succeeded);
        Assert.False(result.Matched);
        Assert.Equal(oversized, result.Text);
        Assert.Equal(0, result.FilteredCharacterCount);
        Assert.Equal(0, nativeCalls);
    }

    [Fact]
    public void Filter_InvalidInputSurrogate_FailsOpen()
    {
        var invalidInput = new string((char)0xD800, 1);
        var filter = CreateFilter(PassthroughExports());
        filter.Refresh();

        var result = filter.Filter(invalidInput);

        Assert.False(result.Succeeded);
        Assert.Equal(invalidInput, result.Text);
    }

    [Fact]
    public void Refresh_RepeatRefreshReloadsAndRemainsUsable()
    {
        var resolverCalls = 0;
        var exports = PassthroughExports();
        var filter = new SteamOfficialTextFilter(() =>
        {
            resolverCalls++;
            return exports;
        });

        Assert.Equal(OfficialTextFilterState.Ready, filter.Refresh().State);
        Assert.Equal(OfficialTextFilterState.Ready, filter.Refresh().State);
        Assert.Equal(2, resolverCalls);
        Assert.True(filter.Filter("hello").Succeeded);
    }

    [Fact]
    public void Filter_ConcurrentCallsRemainThreadSafe()
    {
        var filter = CreateFilter(PassthroughExports());
        filter.Refresh();

        Parallel.For(0, 200, _ =>
            Assert.True(filter.Filter("hello").Succeeded));
    }

    private static SteamOfficialTextFilter CreateFilter(
        SteamOfficialTextFilterExports exports) =>
        new(() => exports);

    private static SteamOfficialTextFilterExports PassthroughExports(bool initResult = true) =>
        new(_ => initResult, PassthroughFilter);

    private static int PassthroughFilter(
        int context,
        ulong sourceSteamId,
        IntPtr input,
        IntPtr output,
        uint capacity)
    {
        CopyCString(input, output, (int)capacity);
        return 0;
    }

    private static string ChineseText(params int[] codePoints) =>
        string.Concat(codePoints.Select(char.ConvertFromUtf32));

    private static string ReadCString(IntPtr pointer, int maxByteCount)
    {
        var bytes = new byte[maxByteCount];
        Marshal.Copy(pointer, bytes, 0, maxByteCount);
        var end = Array.IndexOf(bytes, (byte)0);
        Assert.True(end >= 0, "Input was not NUL terminated.");
        return new UTF8Encoding(false, true).GetString(bytes, 0, end);
    }

    private static void WriteCString(IntPtr pointer, string value, int capacity)
    {
        var bytes = new UTF8Encoding(false, true).GetBytes(value);
        Assert.True(bytes.Length < capacity, "Test output exceeds capacity.");
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        Marshal.WriteByte(pointer, bytes.Length, 0);
    }

    private static void CopyCString(IntPtr source, IntPtr destination, int capacity)
    {
        WriteCString(destination, ReadCString(source, capacity), capacity);
    }
}
