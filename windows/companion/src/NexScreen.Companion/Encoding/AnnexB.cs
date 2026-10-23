namespace NexScreen.Companion.Encoding;

public readonly record struct NalUnit(int Offset, int Length, int Type);

/// <summary>Helpers for H.264 Annex-B byte streams.</summary>
public static class AnnexB
{
    public const int NalSlice = 1;
    public const int NalIdr = 5;
    public const int NalSei = 6;
    public const int NalSps = 7;
    public const int NalPps = 8;
    public const int NalAud = 9;

    private static readonly byte[] StartCode = [0, 0, 0, 1];

    /// <summary>Splits an Annex-B buffer. Offsets point at the NAL header byte (after the start code).</summary>
    public static List<NalUnit> Split(ReadOnlySpan<byte> data)
    {
        var result = new List<NalUnit>();
        var start = FindStartCode(data, 0, out var scLen);
        while (start >= 0)
        {
            var nalStart = start + scLen;
            var next = FindStartCode(data, nalStart, out var nextScLen);
            var nalEnd = next >= 0 ? next : data.Length;

            // Trailing zero bytes belong to the next start code (zero_byte) or are padding.
            while (nalEnd > nalStart && data[nalEnd - 1] == 0)
            {
                nalEnd--;
            }

            if (nalEnd > nalStart)
            {
                result.Add(new NalUnit(nalStart, nalEnd - nalStart, data[nalStart] & 0x1F));
            }

            start = next;
            scLen = nextScLen;
        }

        return result;
    }

    public static bool IsAnnexB(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0 && data[1] == 0 && (data[2] == 1 || (data[2] == 0 && data[3] == 1));

    public static bool ContainsIdr(ReadOnlySpan<byte> data)
    {
        foreach (var nal in Split(data))
        {
            if (nal.Type == NalIdr)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns SPS + PPS NAL units (in stream order, 4-byte start codes), or null if either is missing.</summary>
    public static byte[]? ExtractParameterSets(ReadOnlySpan<byte> data)
    {
        if (!IsAnnexB(data))
        {
            return null;
        }

        using var ms = new MemoryStream();
        bool sps = false, pps = false;
        foreach (var nal in Split(data))
        {
            if (nal.Type is NalSps or NalPps)
            {
                ms.Write(StartCode);
                ms.Write(data.Slice(nal.Offset, nal.Length));
                sps |= nal.Type == NalSps;
                pps |= nal.Type == NalPps;
            }
        }

        return sps && pps ? ms.ToArray() : null;
    }

    /// <summary>Copies the access unit without SPS/PPS, re-emitting every NAL with a 4-byte start code.</summary>
    public static byte[] RemoveParameterSets(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(data.Length);
        foreach (var nal in Split(data))
        {
            if (nal.Type is NalSps or NalPps)
            {
                continue;
            }

            ms.Write(StartCode);
            ms.Write(data.Slice(nal.Offset, nal.Length));
        }

        return ms.ToArray();
    }

    /// <summary>Profile/level from the first SPS (profile_idc, level_idc), for logging.</summary>
    public static (int Profile, int Level)? ReadProfileLevel(ReadOnlySpan<byte> data)
    {
        foreach (var nal in Split(data))
        {
            if (nal.Type == NalSps && nal.Length >= 4)
            {
                return (data[nal.Offset + 1], data[nal.Offset + 3]);
            }
        }

        return null;
    }

    public static string ProfileName(int profileIdc) => profileIdc switch
    {
        66 => "Baseline",
        77 => "Main",
        88 => "Extended",
        100 => "High",
        _ => $"profile_idc {profileIdc}",
    };

    private static int FindStartCode(ReadOnlySpan<byte> data, int from, out int length)
    {
        for (var i = from; i + 2 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0)
            {
                continue;
            }

            if (data[i + 2] == 1)
            {
                // Report a 4-byte start code when preceded by a zero inside the search window.
                if (i > from && data[i - 1] == 0)
                {
                    length = 4;
                    return i - 1;
                }

                length = 3;
                return i;
            }
        }

        length = 0;
        return -1;
    }
}
