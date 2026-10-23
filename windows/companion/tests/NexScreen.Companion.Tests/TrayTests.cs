using Microsoft.Win32;
using NexScreen.Companion.Adb;
using NexScreen.Companion.Tray;

namespace NexScreen.Companion.Tests;

public class TrayTests
{
    [Theory]
    [InlineData(null, false, AdbLinkState.Starting, StatusKind.Waiting)]
    [InlineData(true, false, AdbLinkState.NoDevice, StatusKind.Waiting)]
    [InlineData(true, false, AdbLinkState.Linked, StatusKind.Waiting)]
    [InlineData(true, false, AdbLinkState.Error, StatusKind.Waiting)]
    [InlineData(true, false, AdbLinkState.Disabled, StatusKind.Waiting)]
    [InlineData(true, true, AdbLinkState.Linked, StatusKind.Streaming)]
    [InlineData(true, true, AdbLinkState.Disabled, StatusKind.Streaming)]
    [InlineData(false, true, AdbLinkState.Linked, StatusKind.NoMonitor)]
    [InlineData(false, false, AdbLinkState.NotFound, StatusKind.NoMonitor)]
    [InlineData(true, false, AdbLinkState.NotFound, StatusKind.AdbNotFound)]
    [InlineData(true, false, AdbLinkState.Unauthorized, StatusKind.Unauthorized)]
    [InlineData(null, true, AdbLinkState.Unauthorized, StatusKind.Streaming)]
    public void Classify(bool? output, bool client, AdbLinkState adb, StatusKind expected) =>
        Assert.Equal(expected, StatusText.Classify(new StatusSnapshot(output, client, adb)));

    [Theory]
    [InlineData(StatusKind.Waiting, "NexScreen: aguardando o tablet")]
    [InlineData(StatusKind.Streaming, "NexScreen: transmitindo para o tablet")]
    [InlineData(StatusKind.NoMonitor, "NexScreen: nenhum monitor virtual em modo Estender")]
    [InlineData(StatusKind.AdbNotFound, "NexScreen: adb não encontrado")]
    [InlineData(StatusKind.Unauthorized, "Tablet não autorizado — aceite a depuração USB no tablet")]
    public void Text(StatusKind kind, string text)
    {
        Assert.Equal(text, StatusText.Text(kind));
        Assert.True(text.Length <= 127); // NotifyIcon.Text limit
    }

    [Fact]
    public void Status_ReplacedSessionDoesNotClearConnected()
    {
        var s = new CompanionStatus(adbEnabled: true);
        var changes = 0;
        s.Changed += _ => changes++;
        object a = new(), b = new();
        s.SessionStarted(a);
        s.SessionStarted(b);
        s.SessionEnded(a); // ends after b replaced it
        Assert.True(s.Snapshot.ClientConnected);
        s.SessionEnded(b);
        Assert.False(s.Snapshot.ClientConnected);
        Assert.Equal(2, changes);
        Assert.Equal(AdbLinkState.Disabled, new CompanionStatus(adbEnabled: false).Snapshot.Adb);
    }

    [Theory]
    [InlineData(@"C:\Users\Larissa De Paula\AppData\Local\Programs\NexScreen\NexScreen.Companion.exe",
        @"""C:\Users\Larissa De Paula\AppData\Local\Programs\NexScreen\NexScreen.Companion.exe"" --tray")]
    [InlineData(@"""C:\x\NexScreen.Companion.exe""", @"""C:\x\NexScreen.Companion.exe"" --tray")]
    public void RunKey_CommandLine(string exe, string expected) => Assert.Equal(expected, StartupRegistration.CommandLine(exe));

    [Fact]
    public void RunKey_EnableDisable_OnTestKey()
    {
        // A private test key, never the real Run key.
        const string subKey = @"Software\NexScreen.Tests\Run";
        var reg = new StartupRegistration(subKey, "NexScreenTest");
        const string exe = @"C:\Program Files\Nex Screen\NexScreen.Companion.exe";
        try
        {
            reg.Disable();
            Assert.False(reg.IsEnabled(exe));
            reg.Enable(exe);
            Assert.True(reg.IsEnabled(exe));
            Assert.Equal(StartupRegistration.CommandLine(exe), reg.CurrentValue());
            Assert.False(reg.IsEnabled(@"C:\other\NexScreen.Companion.exe"));
            reg.Disable();
            Assert.Null(reg.CurrentValue());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\NexScreen.Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Options_TrayAndAdb()
    {
        var d = Options.Parse([]);
        Assert.False(d.Tray);
        Assert.True(d.AutoAdb);
        Assert.Null(d.AdbPath);
        var o = Options.Parse(["--tray", "--no-adb", "--adb", @"D:\pt\adb.exe"]);
        Assert.True(o.Tray);
        Assert.False(o.AutoAdb);
        Assert.Equal(@"D:\pt\adb.exe", o.AdbPath);
        Assert.Throws<ArgumentException>(() => Options.Parse(["--adb"]));
    }
}
