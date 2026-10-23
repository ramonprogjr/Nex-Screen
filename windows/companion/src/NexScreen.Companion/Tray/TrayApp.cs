using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using NexScreen.Companion.Displays;
using NexScreen.Companion.Util;

namespace NexScreen.Companion.Tray;

/// <summary>Notification-area icon and menu (--tray). Runs its own STA thread with a message loop.</summary>
public sealed class TrayApp : ApplicationContext
{
    // Session replacement and app restarts drop the connection briefly; only report a lasting disconnect.
    private const int DisconnectNoticeDelayMs = 5000;

    private readonly CompanionStatus _status;
    private readonly AutoExtender _extender;
    private readonly Action _quit;
    private readonly StartupRegistration _startup = new();
    private readonly string _exePath = Environment.ProcessPath ?? Application.ExecutablePath;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _connectionItem;
    private readonly ToolStripMenuItem _keepExtendedItem;
    private readonly ToolStripMenuItem _extendItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly System.Windows.Forms.Timer _disconnectTimer = new() { Interval = DisconnectNoticeDelayMs };
    private readonly SynchronizationContext _ui;
    private bool _announcedConnected;

    private TrayApp(CompanionStatus status, AutoExtender extender, Action quit)
    {
        _status = status;
        _extender = extender;
        _quit = quit;
        _ui = SynchronizationContext.Current!;

        _statusItem = new ToolStripMenuItem { Enabled = false };
        _connectionItem = new ToolStripMenuItem { Enabled = false, Visible = false };
        _extendItem = new ToolStripMenuItem("Estender a área de trabalho", null, (_, _) => ExtendDesktop());
        _keepExtendedItem = new ToolStripMenuItem("Manter a tela do tablet estendida", null,
            (_, _) => _extender.Enabled = !_extender.Enabled);
        _startupItem = new ToolStripMenuItem("Iniciar com o Windows", null, (_, _) => ToggleStartup());
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _statusItem,
            _connectionItem,
            new ToolStripSeparator(),
            _extendItem,
            _keepExtendedItem,
            new ToolStripMenuItem("Abrir pasta de logs", null, (_, _) => OpenLogs()),
            _startupItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Sair", null, (_, _) => _quit()),
        ]);
        menu.Opening += (_, _) => RefreshMenu();

        _icon = new NotifyIcon { Icon = LoadIcon(), ContextMenuStrip = menu, Visible = true };
        _disconnectTimer.Tick += (_, _) =>
        {
            _disconnectTimer.Stop();
            if (_announcedConnected && !_status.Snapshot.ClientConnected)
            {
                _announcedConnected = false;
                _icon.ShowBalloonTip(5000, "Tablet desconectado", "Conecte o tablet pelo cabo USB para continuar.", ToolTipIcon.Info);
            }
        };

        _status.Changed += _ => _ui.Post(_ => Refresh(), null);
        Refresh();
    }

    /// <summary>Starts the tray thread. Dispose the result (from any thread) to remove the icon.</summary>
    public static IDisposable Start(CompanionStatus status, AutoExtender extender, Action quit, SingleInstance? instance)
    {
        TrayApp? app = null;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            app = new TrayApp(status, extender, quit);
            instance?.OnShowRequested(() => app._ui.Post(_ => app.ShowAlreadyRunning(), null));
            ready.Set();
            Application.Run(app);
        })
        {
            Name = "Tray",
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return new Stopper(app!, thread);
    }

    private sealed class Stopper(TrayApp app, Thread thread) : IDisposable
    {
        public void Dispose()
        {
            app._ui.Post(_ => app.ExitThread(), null);
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    protected override void ExitThreadCore()
    {
        _disconnectTimer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }

    private void Refresh()
    {
        var snapshot = _status.Snapshot;
        var text = StatusText.Text(StatusText.Classify(snapshot));
        _icon.Text = text.Length > 127 ? text[..127] : text;
        _statusItem.Text = text;
        _extendItem.Visible = snapshot.OutputAvailable == false;
        var connection = StatusText.ConnectionText(snapshot.Connection);
        _connectionItem.Text = connection ?? string.Empty;
        _connectionItem.Visible = connection is not null;

        if (snapshot.ClientConnected)
        {
            _disconnectTimer.Stop();
            if (!_announcedConnected)
            {
                _announcedConnected = true;
                _icon.ShowBalloonTip(5000, "Tablet conectado", "Transmitindo a tela para o tablet.", ToolTipIcon.Info);
            }
        }
        else if (_announcedConnected && !_disconnectTimer.Enabled)
        {
            _disconnectTimer.Start();
        }
    }

    private void RefreshMenu()
    {
        Refresh();
        _keepExtendedItem.Checked = _extender.Enabled;
        try
        {
            _startupItem.Checked = _startup.IsEnabled(_exePath);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _startupItem.Checked = false;
        }
    }

    private void ToggleStartup()
    {
        try
        {
            if (_startup.IsEnabled(_exePath))
            {
                _startup.Disable();
                Log.Info("Start with Windows: off");
            }
            else
            {
                _startup.Enable(_exePath);
                Log.Info("Start with Windows: on (HKCU Run)");
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Could not change the startup setting: {ex.Message}");
            MessageBox.Show("Não foi possível alterar a inicialização com o Windows.", "NexScreen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowAlreadyRunning() =>
        _icon.ShowBalloonTip(5000, "NexScreen já está aberto", StatusText.Text(StatusText.Classify(_status.Snapshot)), ToolTipIcon.Info);

    private static void ExtendDesktop()
    {
        Log.Info("Tray: extending the desktop (DisplaySwitch /extend)");
        AutoExtender.Extend();
    }

    private static void OpenLogs()
    {
        var dir = Log.DefaultDirectory;
        Directory.CreateDirectory(dir);
        TryStart(new ProcessStartInfo("explorer.exe") { ArgumentList = { dir }, UseShellExecute = false });
    }

    private static void TryStart(ProcessStartInfo psi)
    {
        try
        {
            using var _ = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Could not start {Path.GetFileName(psi.FileName)}: {ex.Message}");
        }
    }

    private static Icon LoadIcon()
    {
        using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("NexScreen.app.ico");
        return stream is null ? (Icon)SystemIcons.Application.Clone() : new Icon(stream, SystemInformation.SmallIconSize);
    }
}
