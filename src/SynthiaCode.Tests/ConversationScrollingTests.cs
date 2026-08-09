using SynthiaCode.App.Views;

[Trait("Category", TestCategories.Wpf)]
[Collection(TestCategories.WpfCollection)]
public sealed class ConversationScrollingTests
{


    [Fact(DisplayName = "chat scrolling follows growing content while pinned to latest")]
    public Task FollowsGrowingContentWhilePinnedAsync()
    {
        var state = new ConversationScrollCoordinator();

        var shouldFollow = state.UpdateFromScroll(
            verticalOffset: 480,
            extentHeight: 1000,
            viewportHeight: 500,
            verticalChange: 0,
            extentHeightChange: 120);

        Assert(state.IsFollowingLatest, "content growth keeps an already pinned transcript following");
        Assert(shouldFollow, "content growth requests one follow-to-latest operation");
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "chat scrolling preserves an intentional scroll-away during streaming")]
    public Task PreservesIntentionalScrollAwayAsync()
    {
        var state = new ConversationScrollCoordinator();

        var shouldFollow = state.UpdateFromScroll(
            verticalOffset: 340,
            extentHeight: 1000,
            viewportHeight: 500,
            verticalChange: -140,
            extentHeightChange: 80);

        Assert(!state.IsFollowingLatest, "an upward user movement pauses auto-follow even while content grows");
        Assert(!shouldFollow, "a paused transcript does not request a jump to the end");

        shouldFollow = state.UpdateFromScroll(
            verticalOffset: 340,
            extentHeight: 1120,
            viewportHeight: 500,
            verticalChange: 0,
            extentHeightChange: 120);

        Assert(!state.IsFollowingLatest, "later streaming growth preserves the reader's position");
        Assert(!shouldFollow, "streaming growth stays quiet while the reader is away from latest");
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "chat scrolling resumes near latest and resets for another chat")]
    public Task ResumesNearLatestAndResetsForAnotherChatAsync()
    {
        var state = new ConversationScrollCoordinator();
        state.UpdateFromScroll(
            verticalOffset: 200,
            extentHeight: 1000,
            viewportHeight: 500,
            verticalChange: -300,
            extentHeightChange: 0);

        var shouldFollow = state.UpdateFromScroll(
            verticalOffset: 442,
            extentHeight: 1000,
            viewportHeight: 500,
            verticalChange: 242,
            extentHeightChange: 0);

        Assert(state.IsFollowingLatest, "scrolling into the comfortable near-latest zone resumes auto-follow");
        Assert(shouldFollow, "resuming near latest settles the transcript at the end");

        state.Pause();
        Assert(!state.IsFollowingLatest, "explicit upward input pauses follow immediately");
        state.FollowLatest();
        Assert(state.IsFollowingLatest, "the Jump to latest action resumes follow");

        state.Pause();
        state.ResetForConversation();
        Assert(state.IsFollowingLatest, "opening another chat starts at its latest message");
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
