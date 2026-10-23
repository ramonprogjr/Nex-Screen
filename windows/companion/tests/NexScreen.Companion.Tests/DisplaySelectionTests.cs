using NexScreen.Companion.Displays;

namespace NexScreen.Companion.Tests;

public class DisplaySelectionTests
{
    private static DisplayOutputInfo Output(string gdi, string name, string? mfr, bool primary = false) =>
        new(gdi, name, mfr, 1728, 1080, 30, primary ? 0 : 3840, 0, primary, "Identity", 0, 0, "GPU", 1, 0x8086);

    private static readonly DisplayOutputInfo Laptop = Output(@"\\.\DISPLAY1", "", "SHP", primary: true);
    private static readonly DisplayOutputInfo Vdd = Output(@"\\.\DISPLAY5", "VDD by MTT", "MTT");
    private static readonly DisplayOutputInfo Nex = Output(@"\\.\DISPLAY7", "NexScreen", "NXS");

    [Fact]
    public void Auto_PrefersNexScreen_ThenVdd()
    {
        Assert.Same(Nex, DisplayEnumerator.Find("auto", [Laptop, Vdd, Nex]));
        Assert.Same(Vdd, DisplayEnumerator.Find("auto", [Laptop, Vdd]));
        Assert.Null(DisplayEnumerator.Find("auto", [Laptop]));
    }

    [Fact]
    public void ExplicitSelectors()
    {
        List<DisplayOutputInfo> all = [Laptop, Vdd, Nex];
        Assert.Same(Vdd, DisplayEnumerator.Find("vdd", all));
        Assert.Same(Nex, DisplayEnumerator.Find("NexScreen", all));
        Assert.Same(Laptop, DisplayEnumerator.Find("primary", all));
        Assert.Same(Vdd, DisplayEnumerator.Find(@"\\.\display5", all));
    }
}
