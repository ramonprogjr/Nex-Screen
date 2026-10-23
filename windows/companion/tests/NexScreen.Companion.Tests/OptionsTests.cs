using NexScreen.Companion.Capture;
using NexScreen.Companion.Encoding;
using NexScreen.Companion.Streaming;

namespace NexScreen.Companion.Tests;

public class OptionsTests
{
    [Fact]
    public void Defaults()
    {
        var o = Options.Parse([]);
        Assert.Equal("nexscreen", o.Output);
        Assert.Equal(30, o.Fps);
        Assert.Equal(8_000_000, o.Bitrate);
        Assert.Equal(300, o.EffectiveGop);
        Assert.Equal(H264Profile.High, o.Profile);
        Assert.Equal(27183, o.Port);
    }

    [Theory]
    [InlineData("8M", 8_000_000)]
    [InlineData("2.5m", 2_500_000)]
    [InlineData("6000k", 6_000_000)]
    [InlineData("4000000", 4_000_000)]
    public void Bitrate(string value, int expected) => Assert.Equal(expected, Options.ParseBitrate(value));

    [Fact]
    public void Parses_AllOptions()
    {
        var o = Options.Parse(["--output", @"\\.\DISPLAY2", "--fps", "60", "--size", "1920x1200", "--level", "4.2",
            "--profile", "main", "--encoder", "software", "--dump", "x.h264", "--seconds", "5"]);
        Assert.Equal(@"\\.\DISPLAY2", o.Output);
        Assert.Equal(60, o.Fps);
        Assert.Equal((1920, 1200), (o.Width!.Value, o.Height!.Value));
        Assert.Equal(42, o.Level);
        Assert.Equal(H264Profile.Main, o.Profile);
        Assert.Equal(EncoderPreference.Software, o.Encoder);
        Assert.Equal("x.h264", o.DumpPath);
        Assert.Equal(5, o.Seconds);
    }

    [Theory]
    [InlineData("--fps", "0")]
    [InlineData("--fps", "61")]
    [InlineData("--size", "1921x1200")]
    [InlineData("--level", "4.3")]
    [InlineData("--bitrate", "10")]
    [InlineData("--bogus", "1")]
    public void Rejects_InvalidValues(string name, string value) =>
        Assert.Throws<ArgumentException>(() => Options.Parse([name, value]));

    [Theory]
    [InlineData(1920, 1200, 1728, 1080)] // NexScreen default mode -> fits the Tab A9+ decoder limit
    [InlineData(3840, 2400, 1728, 1080)]
    [InlineData(1920, 1080, 1920, 1080)] // already within 1920x1088
    [InlineData(1600, 900, 1600, 900)]
    [InlineData(1601, 901, 1600, 900)] // odd sizes are made even
    public void EncodeSize_FitsDecoderLimit(int cw, int ch, int ew, int eh) =>
        Assert.Equal((ew, eh), Options.Parse([]).EncodeSize(cw, ch));

    [Fact]
    public void EncodeSize_ExplicitSizeWins()
    {
        Assert.Equal((1920, 1200), Options.Parse(["--size", "1920x1200"]).EncodeSize(3840, 2400));
        Assert.Equal((1280, 800), Options.Parse(["--max-size", "1280x800"]).EncodeSize(1920, 1200));
    }

    [Fact]
    public void FitRect_Letterboxes()
    {
        var same = Nv12Converter.FitRect(3840, 2400, 1920, 1200);
        Assert.Equal((0, 0, 1920, 1200), (same.Left, same.Top, same.Right, same.Bottom));

        var wide = Nv12Converter.FitRect(1920, 1080, 1920, 1200);
        Assert.Equal((0, 60, 1920, 1140), (wide.Left, wide.Top, wide.Right, wide.Bottom));
    }

    [Fact]
    public void StreamFormat_ComparesConfigBytes()
    {
        var a = new StreamFormat(1920, 1200, 30, [0, 0, 0, 1, 0x67]);
        Assert.True(a.SameAs(new StreamFormat(1920, 1200, 30, [0, 0, 0, 1, 0x67])));
        Assert.False(a.SameAs(new StreamFormat(1920, 1200, 30, [0, 0, 0, 1, 0x68])));
        Assert.False(a.SameAs(new StreamFormat(1600, 900, 30, [0, 0, 0, 1, 0x67])));
        Assert.False(a.SameAs(null));
    }
}
