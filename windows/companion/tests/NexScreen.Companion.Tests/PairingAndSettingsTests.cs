using Microsoft.Win32;
using NexScreen.Companion.Adb;
using NexScreen.Companion.Tray;

namespace NexScreen.Companion.Tests;

public class PairingAndSettingsTests
{
    [Theory]
    [InlineData("123456", true)]
    [InlineData("000000", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("12 456", false)]
    [InlineData("12345a", false)]
    [InlineData("１２３４５６", false)] // full-width digits
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Code_ExactlySixAsciiDigits(string? code, bool valid) => Assert.Equal(valid, PairingInput.IsValidCode(code));

    [Theory]
    [InlineData("", true, null)]
    [InlineData("   ", true, null)]
    [InlineData(null, true, null)]
    [InlineData(" 192.168.0.45:37099 ", true, "192.168.0.45:37099")]
    [InlineData("[fe80::1%wlan0]:40001", true, "[fe80::1%wlan0]:40001")]
    [InlineData("192.168.0.45", false, null)]
    [InlineData("192.168.0.45:0", false, null)]
    [InlineData("192.168.0.45:70000", false, null)]
    [InlineData("tablet:37099", false, null)]
    [InlineData("192.168.0.45:37099 123456", false, null)]
    public void Endpoint_OptionalIpPort(string? text, bool ok, string? endpoint)
    {
        Assert.Equal(ok, PairingInput.TryParseEndpoint(text, out var parsed));
        Assert.Equal(endpoint, parsed);
    }

    [Fact]
    public void ParseMdnsServices_PairingOnly()
    {
        const string output = "List of discovered mdns services\r\n" +
                              "adb-R9XY106J8ZK-1IW1QE\t_adb-tls-connect._tcp\t192.168.0.45:39617\r\n" +
                              "adb-R9XY106J8ZK-1IW1QE\t_adb-tls-pairing._tcp\t192.168.0.45:44219\r\n" +
                              "studio-xyz  _adb-tls-pairing._tcp.  192.168.0.50:37001\n" +
                              "adb-R9XY106J8ZK-1IW1QE\t_adb-tls-pairing._tcp\t192.168.0.45:44219\n" +
                              "bad\t_adb-tls-pairing._tcp\t192.168.0.45:99999\n" +
                              "broken line\n";
        Assert.Equal(["192.168.0.45:44219", "192.168.0.50:37001"], AdbOutput.ParseTlsPairingServices(output));
        Assert.Equal(["192.168.0.45:39617"], AdbOutput.ParseTlsConnectServices(output));
        Assert.Empty(AdbOutput.ParseTlsPairingServices("List of discovered mdns services\n"));
        Assert.Empty(AdbOutput.ParseTlsPairingServices("error: unknown command mdns\n"));
    }

    [Fact]
    public void PairResult_SuccessAndFailuresNeverShowTheCode()
    {
        const string code = "482913";
        var ok = AdbPairing.Interpret(new AdbResult(0,
            "Enter pairing code: Successfully paired to 192.168.0.45:44219 [guid=adb-R9XY106J8ZK-1IW1QE]\n", false), code);
        Assert.True(ok.Ok);

        var wrong = AdbPairing.Interpret(new AdbResult(1, "Enter pairing code: Failed: Wrong password or connection was dropped.\n", false), code);
        Assert.False(wrong.Ok);
        Assert.Contains("Código incorreto", wrong.Message);

        var fault = AdbPairing.Interpret(new AdbResult(1,
            "Enter pairing code: * daemon started successfully\nerror: protocol fault (couldn't read status message): No error\n", false), code);
        Assert.False(fault.Ok);
        Assert.Equal("Não foi possível parear: error: protocol fault (couldn't read status message): No error", fault.Message);

        var echoed = AdbPairing.Interpret(new AdbResult(1, $"error: unexpected argument {code}\n", false), code);
        Assert.DoesNotContain(code, echoed.Message);

        var timeout = AdbPairing.Interpret(new AdbResult(-1, string.Empty, true), code);
        Assert.False(timeout.Ok);
        Assert.Contains("não respondeu", timeout.Message);

        // Exit code 0 without the success line is not a success.
        Assert.False(AdbPairing.Interpret(new AdbResult(0, "Enter pairing code: \n", false), code).Ok);
    }

    [Fact]
    public void Pair_RejectsInvalidCodeBeforeRunningAdb() =>
        Assert.False(AdbPairing.Pair(@"C:\does-not-exist\adb.exe", null, "12345").Ok);

    [Fact]
    public void KeepExtended_PersistsOnTestKey()
    {
        // A private test key, never the real HKCU\Software\NexScreen; not the Run test's key (classes run in parallel).
        const string root = @"Software\NexScreen.SettingsTests";
        const string subKey = root + @"\Settings";
        var settings = new TraySettings(subKey);
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
            Assert.True(settings.KeepExtended); // default: on

            settings.KeepExtended = false;
            Assert.False(new TraySettings(subKey).KeepExtended); // survives a "restart" (new instance)
            using (var key = Registry.CurrentUser.OpenSubKey(subKey))
            {
                Assert.Equal(RegistryValueKind.DWord, key!.GetValueKind(TraySettings.KeepExtendedValue));
                Assert.Equal(0, key.GetValue(TraySettings.KeepExtendedValue));
            }

            settings.KeepExtended = true;
            Assert.True(new TraySettings(subKey).KeepExtended);

            // A value of the wrong type falls back to the default.
            using (var key = Registry.CurrentUser.CreateSubKey(subKey))
            {
                key.SetValue(TraySettings.KeepExtendedValue, "0", RegistryValueKind.String);
            }

            Assert.True(settings.KeepExtended);

            Assert.Null(settings.VirtualMonitorRect);
            settings.VirtualMonitorRect = new Displays.DesktopRect(-1728, 174, 1728, 1080);
            Assert.Equal(new Displays.DesktopRect(-1728, 174, 1728, 1080), new TraySettings(subKey).VirtualMonitorRect);
            settings.VirtualMonitorRect = null;
            Assert.Null(settings.VirtualMonitorRect);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }

        Assert.Null(Registry.CurrentUser.OpenSubKey(root));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)] // --no-auto-extend wins for the run
    [InlineData(false, false, false)]
    public void KeepExtended_CommandLineWins(bool commandLineAllows, bool saved, bool expected) =>
        Assert.Equal(expected, TraySettings.EffectiveKeepExtended(commandLineAllows, saved));
}
