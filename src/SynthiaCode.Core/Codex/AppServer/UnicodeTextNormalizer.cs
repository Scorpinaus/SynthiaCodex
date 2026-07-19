using System.Text;

namespace SynthiaCode.Core.Codex.AppServer;

public static class UnicodeTextNormalizer
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static string RepairLegacyMojibake(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var repaired = new StringBuilder(value.Length);
        var windows1252Run = new List<byte>(value.Length);
        var originalRun = new StringBuilder(value.Length);

        void FlushRun()
        {
            if (originalRun.Length == 0)
            {
                return;
            }

            repaired.Append(RepairRun(originalRun.ToString(), windows1252Run));
            originalRun.Clear();
            windows1252Run.Clear();
        }

        foreach (var character in value)
        {
            if (TryEncodeWindows1252(character, out var encoded))
            {
                originalRun.Append(character);
                windows1252Run.Add(encoded);
                continue;
            }

            FlushRun();
            repaired.Append(character);
        }

        FlushRun();
        return repaired.ToString();
    }

    public static CodexTimelineItem RepairLegacyMojibake(CodexTimelineItem item) => item with
    {
        Title = RepairLegacyMojibake(item.Title),
        Detail = RepairLegacyMojibake(item.Detail)
    };

    private static string RepairRun(string original, List<byte> bytes)
    {
        var originalScore = CountMojibakeMarkers(original);
        if (originalScore == 0)
        {
            return original;
        }

        string candidate;
        try
        {
            candidate = StrictUtf8.GetString([.. bytes]);
        }
        catch (DecoderFallbackException)
        {
            return original;
        }

        if (candidate.Contains('\uFFFD') ||
            CountMojibakeMarkers(candidate) >= originalScore ||
            !string.Equals(EncodeUtf8AsWindows1252(candidate), original, StringComparison.Ordinal))
        {
            return original;
        }

        return candidate;
    }

    private static int CountMojibakeMarkers(string value)
    {
        var score = 0;
        for (var index = 0; index < value.Length - 1; index++)
        {
            var current = value[index];
            var next = value[index + 1];
            if ((current == '\u00E2' && IsWindows1252Punctuation(next)) ||
                (current == '\u00C3' && next is >= '\u0080' and <= '\u00BF') ||
                (current == '\u00C2' && (next is >= '\u0080' and <= '\u00BF' || char.IsWhiteSpace(next))) ||
                (current == '\u00F0' && next is '\u0178' or '\u009F') ||
                (current is '\u00D0' or '\u00D1' &&
                 (next is >= '\u0080' and <= '\u00BF' || IsWindows1252Punctuation(next))))
            {
                score++;
            }
        }

        return score;
    }

    private static bool IsWindows1252Punctuation(char value) =>
        (value is '\u20AC' or '\u201A' or '\u0192' or '\u201E' or '\u2026' or '\u2020' or '\u2021' or
            '\u02C6' or '\u2030' or '\u0160' or '\u2039' or '\u0152' or '\u017D' or '\u2018' or
            '\u2019' or '\u201C' or '\u201D' or '\u2022' or '\u2013' or '\u2014' or '\u02DC' or
            '\u2122' or '\u0161' or '\u203A' or '\u0153' or '\u017E' or '\u0178') ||
        value is >= '\u0080' and <= '\u00BF';

    private static string? EncodeUtf8AsWindows1252(string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        var result = new StringBuilder(bytes.Length);
        foreach (var valueByte in bytes)
        {
            if (!TryDecodeWindows1252(valueByte, out var decoded))
            {
                return null;
            }

            result.Append(decoded);
        }

        return result.ToString();
    }

    private static bool TryEncodeWindows1252(char value, out byte encoded)
    {
        if (value <= '\u00FF')
        {
            encoded = (byte)value;
            return true;
        }

        encoded = value switch
        {
            '\u20AC' => 0x80,
            '\u201A' => 0x82,
            '\u0192' => 0x83,
            '\u201E' => 0x84,
            '\u2026' => 0x85,
            '\u2020' => 0x86,
            '\u2021' => 0x87,
            '\u02C6' => 0x88,
            '\u2030' => 0x89,
            '\u0160' => 0x8A,
            '\u2039' => 0x8B,
            '\u0152' => 0x8C,
            '\u017D' => 0x8E,
            '\u2018' => 0x91,
            '\u2019' => 0x92,
            '\u201C' => 0x93,
            '\u201D' => 0x94,
            '\u2022' => 0x95,
            '\u2013' => 0x96,
            '\u2014' => 0x97,
            '\u02DC' => 0x98,
            '\u2122' => 0x99,
            '\u0161' => 0x9A,
            '\u203A' => 0x9B,
            '\u0153' => 0x9C,
            '\u017E' => 0x9E,
            '\u0178' => 0x9F,
            _ => 0
        };
        return encoded != 0;
    }

    private static bool TryDecodeWindows1252(byte value, out char decoded)
    {
        decoded = value switch
        {
            0x80 => '\u20AC',
            0x82 => '\u201A',
            0x83 => '\u0192',
            0x84 => '\u201E',
            0x85 => '\u2026',
            0x86 => '\u2020',
            0x87 => '\u2021',
            0x88 => '\u02C6',
            0x89 => '\u2030',
            0x8A => '\u0160',
            0x8B => '\u2039',
            0x8C => '\u0152',
            0x8E => '\u017D',
            0x91 => '\u2018',
            0x92 => '\u2019',
            0x93 => '\u201C',
            0x94 => '\u201D',
            0x95 => '\u2022',
            0x96 => '\u2013',
            0x97 => '\u2014',
            0x98 => '\u02DC',
            0x99 => '\u2122',
            0x9A => '\u0161',
            0x9B => '\u203A',
            0x9C => '\u0153',
            0x9E => '\u017E',
            0x9F => '\u0178',
            _ => (char)value
        };
        return value is not (0x81 or 0x8D or 0x8F or 0x90 or 0x9D);
    }
}
