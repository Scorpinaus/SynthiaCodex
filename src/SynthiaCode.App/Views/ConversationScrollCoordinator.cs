namespace SynthiaCode.App.Views;

public sealed class ConversationScrollCoordinator
{
    public const double NearLatestThreshold = 72;

    public bool IsFollowingLatest { get; private set; } = true;

    public bool UpdateFromScroll(
        double verticalOffset,
        double extentHeight,
        double viewportHeight,
        double verticalChange,
        double extentHeightChange,
        double viewportHeightChange = 0)
    {
        var distanceFromLatest = Math.Max(0, extentHeight - viewportHeight - verticalOffset);

        if (verticalChange < 0 && distanceFromLatest > NearLatestThreshold)
        {
            IsFollowingLatest = false;
            return false;
        }

        var resumedNearLatest = !IsFollowingLatest && distanceFromLatest <= NearLatestThreshold;
        if (distanceFromLatest <= NearLatestThreshold)
        {
            IsFollowingLatest = true;
        }

        return IsFollowingLatest &&
               (resumedNearLatest || extentHeightChange != 0 || viewportHeightChange != 0);
    }

    public void Pause() => IsFollowingLatest = false;

    public void FollowLatest() => IsFollowingLatest = true;

    public void ResetForConversation() => IsFollowingLatest = true;
}
