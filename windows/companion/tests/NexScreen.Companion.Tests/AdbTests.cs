using NexScreen.Companion.Adb;

namespace NexScreen.Companion.Tests;

public class AdbTests
{
    [Fact]
    public void ParseDevices_LongFormat()
    {
        const string output = "List of devices attached\r\n" +
                              "R9XY106J8ZK            device product:gta9pxxx model:SM_X216B device:gta9p transport_id:1\r\n" +
                              "emulator-5554          offline transport_id:4\r\n" +
                              "\r\n";
        var d = AdbOutput.ParseDevices(output);
        Assert.Equal(2, d.Count);
        Assert.Equal(new AdbDevice("R9XY106J8ZK", AdbDeviceState.Device, "device", "1"), d[0]);
        Assert.True(d[0].IsReady);
        Assert.Equal(AdbDeviceState.Offline, d[1].State);
        Assert.Equal("4", d[1].TransportId);
        Assert.False(d[1].IsReady);
    }

    [Fact]
    public void ParseDevices_ShortFormat_Unauthorized_And_DaemonStartup()
    {
        const string output = "* daemon not running; starting now at tcp:5037\n" +
                              "* daemon started successfully\n" +
                              "List of devices attached\n" +
                              "R9XY106J8ZK\tunauthorized\n" +
                              "0123456789ABCDEF\tdevice\n";
        var d = AdbOutput.ParseDevices(output);
        Assert.Equal(2, d.Count);
        Assert.Equal(AdbDeviceState.Unauthorized, d[0].State);
        Assert.Null(d[0].TransportId);
        Assert.Equal(AdbDeviceState.Device, d[1].State);
    }

    [Fact]
    public void ParseDevices_NoPermissions_And_Other()
    {
        const string output = "List of devices attached\n" +
                              "0123456789ABCDEF       no permissions (missing udev rules? user is in the plugdev group); see [http://developer.android.com/tools/device.html] usb:1-1 transport_id:7\n" +
                              "ZY22                   recovery usb:1-2 transport_id:8\n" +
                              "QQ11                   unauthorized usb:1-3 transport_id:9\n";
        var d = AdbOutput.ParseDevices(output);
        Assert.Equal(3, d.Count);
        Assert.Equal(AdbDeviceState.NoPermissions, d[0].State);
        Assert.Equal("no permissions", d[0].RawState);
        Assert.Equal("7", d[0].TransportId);
        Assert.Equal(AdbDeviceState.Other, d[1].State);
        Assert.Equal("recovery", d[1].RawState);
        Assert.Equal(AdbDeviceState.Unauthorized, d[2].State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("List of devices attached\r\n\r\n")]
    [InlineData("adb server version (41) doesn't match this client (39); killing...\nerror: cannot connect to daemon\n")]
    public void ParseDevices_Empty(string output) => Assert.Empty(AdbOutput.ParseDevices(output));

    [Theory]
    [InlineData("UsbFfs tcp:27183 tcp:27183\n", 27183, true)]
    [InlineData("host-19 tcp:27183 tcp:27183\r\n", 27183, true)]
    [InlineData("UsbFfs tcp:27184 tcp:27183\n", 27183, false)]
    [InlineData("", 27183, false)]
    public void HasReverse(string output, int port, bool expected) => Assert.Equal(expected, AdbOutput.HasReverse(output, port));

    [Theory]
    [InlineData("Starting: Intent { cmp=app.nexscreen/.MainActivity }", false)]
    [InlineData("Starting: Intent { cmp=app.nexscreen/.MainActivity }\nWarning: Activity not started, intent has been delivered to currently running top-most instance.", false)]
    [InlineData("Starting: Intent { cmp=app.nexscreen/.MainActivity }\nError type 3\nError: Activity class {app.nexscreen/app.nexscreen.MainActivity} does not exist.", true)]
    public void AmStartFailed(string output, bool failed) => Assert.Equal(failed, AdbOutput.AmStartFailed(output));

    [Theory]
    [InlineData("R9XY106J8ZK", "...J8ZK")]
    [InlineData("AB1", "AB1")]
    public void MaskSerial(string serial, string masked) => Assert.Equal(masked, AdbOutput.MaskSerial(serial));

    private const string LocalAppData = @"C:\Users\u\AppData\Local";
    private const string WinGetDir = LocalAppData + @"\Microsoft\WinGet\Packages\Google.PlatformTools_Microsoft.Winget.Source_8wekyb3d8bbwe";

    private static IEnumerable<string> FakeDirs(string path, string pattern) =>
        path == LocalAppData + @"\Microsoft\WinGet\Packages" && pattern == "*PlatformTools*" ? [WinGetDir] : [];

    [Fact]
    public void Locator_SearchOrder()
    {
        var c = AdbLocator.Candidates(null, @"C:\Windows;""C:\Tools\adb"";relative\dir;;", LocalAppData, FakeDirs).ToList();
        Assert.Equal(
        [
            @"C:\Windows\adb.exe",
            @"C:\Tools\adb\adb.exe",
            LocalAppData + @"\Android\Sdk\platform-tools\adb.exe",
            WinGetDir + @"\platform-tools\adb.exe",
        ], c);
    }

    [Fact]
    public void Locator_PathWinsOverSdkAndWinGet()
    {
        string[] existing = [@"C:\Tools\adb.exe", LocalAppData + @"\Android\Sdk\platform-tools\adb.exe", WinGetDir + @"\platform-tools\adb.exe"];
        Assert.Equal(@"C:\Tools\adb.exe", AdbLocator.Find(null, @"C:\Windows;C:\Tools", LocalAppData, existing.Contains, FakeDirs));
    }

    [Fact]
    public void Locator_FallsBackToWinGet()
    {
        var winget = WinGetDir + @"\platform-tools\adb.exe";
        Assert.Equal(winget, AdbLocator.Find(null, @"C:\Windows", LocalAppData, p => p == winget, FakeDirs));
        Assert.Null(AdbLocator.Find(null, @"C:\Windows", LocalAppData, _ => false, FakeDirs));
    }

    [Fact]
    public void Locator_ExplicitPathIsTheOnlyCandidate()
    {
        var c = AdbLocator.Candidates(@"""D:\sdk\adb.exe""", @"C:\Tools", LocalAppData, FakeDirs).ToList();
        Assert.Equal([@"D:\sdk\adb.exe"], c);
        Assert.Null(AdbLocator.Find(@"D:\missing\adb.exe", @"C:\Tools", LocalAppData, p => p == @"C:\Tools\adb.exe", FakeDirs));
    }
}
