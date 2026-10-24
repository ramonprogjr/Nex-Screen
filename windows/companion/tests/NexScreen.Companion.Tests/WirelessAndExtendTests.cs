using NexScreen.Companion.Adb;
using NexScreen.Companion.Displays;
using NexScreen.Companion.Tray;
using Target = NexScreen.Companion.Displays.DisplayConfigInterop.MonitorTarget;

namespace NexScreen.Companion.Tests;

public class WirelessAndExtendTests
{
    private const string Mdns = "adb-R9XY106J8ZK-1IW1QE._adb-tls-connect._tcp";

    [Theory]
    [InlineData("R9XY106J8ZK", AdbConnection.Usb)]
    [InlineData("192.168.0.45:39617", AdbConnection.Wifi)]
    [InlineData("[fe80::1%wlan0]:5555", AdbConnection.Wifi)]
    [InlineData(Mdns, AdbConnection.Wifi)]
    [InlineData("adb-XYZ._adb._tcp", AdbConnection.Wifi)]
    [InlineData("emulator-5554", AdbConnection.Usb)]
    public void ConnectionOf(string serial, AdbConnection expected) => Assert.Equal(expected, AdbOutput.ConnectionOf(serial));

    [Fact]
    public void ParseDevices_WirelessSerials()
    {
        const string output = "List of devices attached\r\n" +
                              "192.168.0.45:39617     device product:gta9pxxx model:SM_X216B device:gta9p transport_id:3\r\n" +
                              Mdns + " device product:gta9pxxx model:SM_X216B device:gta9p transport_id:2\r\n";
        var d = AdbOutput.ParseDevices(output);
        Assert.Equal(["192.168.0.45:39617", Mdns], d.Select(x => x.Serial));
        Assert.All(d, x => Assert.True(x.IsReady));
        Assert.Equal(["3", "2"], d.Select(x => x.TransportId));
    }

    private static AdbDevice Ready(string serial) => new(serial, AdbDeviceState.Device, "device", "1");

    [Fact]
    public void Dedup_SameTabletTwice_PrefersIpPortOverMdns()
    {
        var chosen = AdbOutput.ChoosePerPhysicalDevice([(Ready(Mdns), "R9XY106J8ZK"), (Ready("192.168.0.45:39617"), "R9XY106J8ZK")]);
        Assert.Equal(["192.168.0.45:39617"], chosen.Select(c => c.Serial));
    }

    [Fact]
    public void Dedup_PrefersUsb_KeepsOtherDevices()
    {
        var chosen = AdbOutput.ChoosePerPhysicalDevice(
        [
            (Ready("192.168.0.45:39617"), "R9XY106J8ZK"),
            (Ready("R9XY106J8ZK"), "R9XY106J8ZK"),
            (Ready(Mdns), "R9XY106J8ZK"),
            (Ready("emulator-5554"), "EMULATOR"),
        ]);
        Assert.Equal(["R9XY106J8ZK", "emulator-5554"], chosen.Select(c => c.Serial));
        Assert.Empty(AdbOutput.ChoosePerPhysicalDevice([]));
    }

    [Fact]
    public void ParseMdnsServices_OnlyTlsConnect()
    {
        const string output = "List of discovered mdns services\r\n" +
                              "adb-R9XY106J8ZK-1IW1QE\t_adb-tls-connect._tcp\t192.168.0.45:39617\r\n" +
                              "adb-R9XY106J8ZK-1IW1QE\t_adb-tls-pairing._tcp\t192.168.0.45:44219\r\n" +
                              "adb-OTHER  _adb-tls-connect._tcp.  192.168.0.50:41000\n" +
                              "adb-legacy\t_adb._tcp\t192.168.0.51:5555\n" +
                              "adb-R9XY106J8ZK-1IW1QE\t_adb-tls-connect._tcp\t192.168.0.45:39617\n" +
                              "broken line\n";
        Assert.Equal(["192.168.0.45:39617", "192.168.0.50:41000"], AdbOutput.ParseTlsConnectServices(output));
        Assert.Empty(AdbOutput.ParseTlsConnectServices("List of discovered mdns services\n"));
        Assert.Empty(AdbOutput.ParseTlsConnectServices("error: unknown command mdns\n"));
    }

    [Theory]
    [InlineData(AdbConnection.Usb, "Tablet conectado por USB")]
    [InlineData(AdbConnection.Wifi, "Tablet conectado por Wi-Fi")]
    [InlineData(AdbConnection.None, null)]
    public void ConnectionText(AdbConnection c, string? text) => Assert.Equal(text, StatusText.ConnectionText(c));

    [Fact]
    public void TabletPresent()
    {
        Assert.True(new StatusSnapshot(false, false, AdbLinkState.Linked).TabletPresent);
        Assert.True(new StatusSnapshot(false, true, AdbLinkState.Disabled).TabletPresent);
        Assert.False(new StatusSnapshot(false, false, AdbLinkState.NoDevice).TabletPresent);
    }

    [Fact]
    public void Detached_VddConnectedButInactive()
    {
        Target[] targets = [new("", "SHP", true), new("VDD by MTT", "MTT", false)];
        Assert.True(DisplayEnumerator.IsVirtualMonitorDetached("auto", targets));
        Assert.True(DisplayEnumerator.IsVirtualMonitorDetached("VDD", targets));
        Assert.False(DisplayEnumerator.IsVirtualMonitorDetached("nexscreen", targets));
        Assert.False(DisplayEnumerator.IsVirtualMonitorDetached("primary", targets));
        Assert.False(DisplayEnumerator.IsVirtualMonitorDetached(@"\\.\DISPLAY5", targets));
    }

    [Fact]
    public void Connected_CountsActiveDuplicatedAndInactive()
    {
        // "Duplicate" mode: the VDD path is active but mirrors the main screen
        Assert.True(DisplayEnumerator.IsVirtualMonitorConnected("auto", [new("", "SHP", true), new("VDD by MTT", "MTT", true)]));
        Assert.True(DisplayEnumerator.IsVirtualMonitorConnected("auto", [new("NexScreen", "NXS", false)]));
        Assert.False(DisplayEnumerator.IsVirtualMonitorConnected("auto", [new("", "SHP", true)]));
        Assert.False(DisplayEnumerator.IsVirtualMonitorConnected("primary", [new("VDD by MTT", "MTT", false)]));
        Assert.False(DisplayEnumerator.IsVirtualMonitorConnected("nexscreen", [new("VDD by MTT", "MTT", true)]));
    }

    [Fact]
    public void Detached_FalseWhenActiveOrAbsent()
    {
        Assert.False(DisplayEnumerator.IsVirtualMonitorDetached("auto", [new("", "SHP", true), new("VDD by MTT", "MTT", true)]));
        Assert.False(DisplayEnumerator.IsVirtualMonitorDetached("auto", [new("", "SHP", true)]));
        Assert.True(DisplayEnumerator.IsVirtualMonitorDetached("auto", [new("NexScreen", "NXS", false)]));
    }

    [Fact]
    public void ExtendPolicy_RateLimitedAndGivesUp()
    {
        var p = new ExtendPolicy();
        var t = new DateTime(2026, 10, 23, 20, 0, 0, DateTimeKind.Utc);
        Assert.False(p.ShouldExtend(t, enabled: true, tabletPresent: false, monitorDetached: true)); // no tablet
        Assert.False(p.ShouldExtend(t, enabled: false, tabletPresent: true, monitorDetached: true)); // turned off
        Assert.False(p.ShouldExtend(t, enabled: true, tabletPresent: true, monitorDetached: false)); // attached
        Assert.True(p.ShouldExtend(t, true, true, true));
        Assert.False(p.ShouldExtend(t.AddSeconds(10), true, true, true)); // < 30 s
        Assert.True(p.ShouldExtend(t.AddSeconds(30), true, true, true));
        Assert.True(p.ShouldExtend(t.AddSeconds(60), true, true, true));
        Assert.True(p.GaveUp); // 3 tries, still detached
        Assert.False(p.ShouldExtend(t.AddSeconds(600), true, true, true));
        p.Reset(); // tablet reconnected
        Assert.True(p.ShouldExtend(t.AddSeconds(630), true, true, true));
    }

    [Fact]
    public void ExtendPolicy_AttachResetsAttempts()
    {
        var p = new ExtendPolicy();
        var t = DateTime.UtcNow;
        for (var i = 0; i < ExtendPolicy.MaxAttempts; i++)
        {
            Assert.True(p.ShouldExtend(t.AddMinutes(i), true, true, true));
        }

        Assert.True(p.GaveUp);
        Assert.False(p.ShouldExtend(t.AddMinutes(10), true, true, false)); // monitor came back
        Assert.False(p.GaveUp);
        Assert.True(p.ShouldExtend(t.AddMinutes(11), true, true, true));
    }

    [Fact]
    public void Options_NoAutoExtend()
    {
        Assert.True(Options.Parse([]).AutoExtend);
        Assert.False(Options.Parse(["--no-auto-extend"]).AutoExtend);
    }
}
