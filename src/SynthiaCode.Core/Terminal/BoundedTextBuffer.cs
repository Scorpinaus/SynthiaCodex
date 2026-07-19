namespace SynthiaCode.Core.Terminal;

public sealed class BoundedTextBuffer
{
    private readonly object syncRoot = new();
    private readonly char[] buffer;
    private int start;
    private int count;

    public BoundedTextBuffer(int maximumCharacters)
    {
        if (maximumCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        }

        MaximumCharacters = maximumCharacters;
        buffer = new char[maximumCharacters];
    }

    public int MaximumCharacters { get; }

    public int Length
    {
        get
        {
            lock (syncRoot)
            {
                return count;
            }
        }
    }

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (syncRoot)
        {
            if (text.Length >= MaximumCharacters)
            {
                text.AsSpan(text.Length - MaximumCharacters).CopyTo(buffer);
                start = 0;
                count = MaximumCharacters;
                return;
            }

            var overflow = Math.Max(0, count + text.Length - MaximumCharacters);
            start = (start + overflow) % MaximumCharacters;
            count -= overflow;

            var writeIndex = (start + count) % MaximumCharacters;
            var firstCopyLength = Math.Min(text.Length, MaximumCharacters - writeIndex);
            text.AsSpan(0, firstCopyLength).CopyTo(buffer.AsSpan(writeIndex));
            text.AsSpan(firstCopyLength).CopyTo(buffer);
            count += text.Length;
        }
    }

    public void Clear()
    {
        lock (syncRoot)
        {
            start = 0;
            count = 0;
        }
    }

    public string Snapshot()
    {
        lock (syncRoot)
        {
            if (count == 0)
            {
                return string.Empty;
            }

            var snapshot = new char[count];
            var firstCopyLength = Math.Min(count, MaximumCharacters - start);
            buffer.AsSpan(start, firstCopyLength).CopyTo(snapshot);
            buffer.AsSpan(0, count - firstCopyLength).CopyTo(snapshot.AsSpan(firstCopyLength));
            return new string(snapshot);
        }
    }
}
