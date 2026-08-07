using GBFR.ChatOverlay.Core;

namespace GBFR.ChatOverlay.Tests;

public sealed class ChatComposerTests
{
    [Fact]
    public void Draft_NormalizesLineBreaksAndPreservesUnicode()
    {
        var composer = new ChatComposer();

        composer.SetDraft("你好\r\nRelink");

        Assert.Equal("你好 Relink", composer.Draft);
    }

    [Fact]
    public void Draft_TruncatesWithoutSplittingUnicodeScalar()
    {
        var composer = new ChatComposer(maximumDraftLength: 2);

        composer.SetDraft("A😀B");

        Assert.Equal("A😀", composer.Draft);
    }

    [Fact]
    public void VoiceCapture_RequiresReviewBeforeSubmission()
    {
        var composer = new ChatComposer();

        Assert.True(composer.BeginVoiceCapture());
        Assert.False(composer.OpenKeyboard());
        Assert.True(composer.CompleteVoiceCapture("准备好了"));

        Assert.Equal(ChatInputMode.VoiceReview, composer.Mode);
        Assert.True(composer.TryGetSubmittableText(out var text));
        Assert.Equal("准备好了", text);
    }

    [Fact]
    public void Cancel_PreservesDraftUnlessRoomScopeRequestsClear()
    {
        var composer = new ChatComposer();
        composer.OpenKeyboard();
        composer.SetDraft("unfinished");

        composer.Cancel();
        Assert.Equal("unfinished", composer.Draft);

        composer.Cancel(clearDraft: true);
        Assert.Empty(composer.Draft);
    }
}
