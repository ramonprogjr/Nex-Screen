using NexScreen.Companion.Encoding;

namespace NexScreen.Companion.Tests;

public class AnnexBTests
{
    private static readonly byte[] Sps = [0x67, 0x64, 0x00, 0x32, 0xAC, 0x2B];
    private static readonly byte[] Pps = [0x68, 0xEE, 0x3C, 0xB0];
    private static readonly byte[] Aud = [0x09, 0x10];
    private static readonly byte[] Sei = [0x06, 0x05, 0x01, 0x80];
    private static readonly byte[] Idr = [0x65, 0x88, 0x84, 0x00, 0x21];
    private static readonly byte[] Slice = [0x41, 0x9A, 0x02, 0x04];

    private static byte[] Sc4(params byte[][] nals) => nals.SelectMany(n => new byte[] { 0, 0, 0, 1 }.Concat(n)).ToArray();

    [Fact]
    public void Split_Handles3And4ByteStartCodes()
    {
        byte[] data = [0, 0, 0, 1, .. Sps, 0, 0, 1, .. Pps, 0, 0, 0, 1, .. Idr];
        var nals = AnnexB.Split(data);

        Assert.Equal([AnnexB.NalSps, AnnexB.NalPps, AnnexB.NalIdr], nals.Select(n => n.Type));
        Assert.Equal(Sps, data.AsSpan(nals[0].Offset, nals[0].Length).ToArray());
        Assert.Equal(Pps, data.AsSpan(nals[1].Offset, nals[1].Length).ToArray());
        Assert.Equal(Idr, data.AsSpan(nals[2].Offset, nals[2].Length).ToArray());
    }

    [Fact]
    public void Split_KeepsZeroBytesInsideNal()
    {
        // 00 00 03 is an emulation-prevention sequence and must not split the NAL.
        byte[] nal = [0x65, 0x00, 0x00, 0x03, 0x01, 0x42];
        var nals = AnnexB.Split(Sc4(nal));
        Assert.Single(nals);
        Assert.Equal(nal.Length, nals[0].Length);
    }

    [Fact]
    public void ExtractParameterSets_FromIdrAccessUnit()
    {
        var au = Sc4(Aud, Sps, Pps, Sei, Idr);
        Assert.Equal(Sc4(Sps, Pps), AnnexB.ExtractParameterSets(au));
    }

    [Fact]
    public void ExtractParameterSets_NullWhenIncompleteOrNotAnnexB()
    {
        Assert.Null(AnnexB.ExtractParameterSets(Sc4(Sps, Idr)));
        Assert.Null(AnnexB.ExtractParameterSets(Sc4(Slice)));
        Assert.Null(AnnexB.ExtractParameterSets(Sps)); // no start code (e.g. avcC payload)
        Assert.Null(AnnexB.ExtractParameterSets([]));
    }

    [Fact]
    public void RemoveParameterSets_KeepsOtherNals()
    {
        var au = Sc4(Aud, Sps, Pps, Sei, Idr);
        Assert.Equal(Sc4(Aud, Sei, Idr), AnnexB.RemoveParameterSets(au));
        Assert.Equal(Sc4(Slice), AnnexB.RemoveParameterSets(Sc4(Slice)));
    }

    [Fact]
    public void KeyframeDetection()
    {
        Assert.True(AnnexB.ContainsIdr(Sc4(Sps, Pps, Idr)));
        Assert.True(AnnexB.ContainsIdr(Sc4(Idr)));
        Assert.False(AnnexB.ContainsIdr(Sc4(Slice)));
        Assert.False(AnnexB.ContainsIdr(Sc4(Sps, Pps)));
        Assert.False(AnnexB.ContainsIdr([]));
    }

    [Fact]
    public void ReadProfileLevel_FromSps()
    {
        var pl = AnnexB.ReadProfileLevel(Sc4(Sps, Pps));
        Assert.Equal((100, 50), pl);
        Assert.Equal("High", AnnexB.ProfileName(pl!.Value.Profile));
        Assert.Null(AnnexB.ReadProfileLevel(Sc4(Idr)));
    }
}
