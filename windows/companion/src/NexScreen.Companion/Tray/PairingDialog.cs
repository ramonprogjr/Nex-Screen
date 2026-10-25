using System.Drawing;
using System.Windows.Forms;
using NexScreen.Companion.Adb;

namespace NexScreen.Companion.Tray;

/// <summary>"Parear tablet por Wi-Fi": pairing code (and optional IP:porta) -> adb pair, off the UI thread.</summary>
public sealed class PairingDialog : Form
{
    public const string Instructions =
        "No tablet: Configurações → Opções do desenvolvedor → Depuração por Wi-Fi → Parear dispositivo com código de pareamento";

    private readonly string? _adbPath;
    private readonly Action _onPaired;
    private readonly TextBox _code;
    private readonly TextBox _endpoint;
    private readonly Label _status;
    private readonly Button _pair;
    private bool _busy;

    public PairingDialog(string? adbPath, Action onPaired)
    {
        _adbPath = adbPath;
        _onPaired = onPaired;

        Text = "Parear tablet por Wi-Fi";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        // Sizes below are logical pixels, scaled to the monitor DPI by D().
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = SystemFonts.MessageBoxFont ?? Font;
        using (var stream = typeof(PairingDialog).Assembly.GetManifestResourceStream("NexScreen.app.ico"))
        {
            if (stream is not null)
            {
                Icon = new Icon(stream);
            }
        }

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(D(12)),
            Dock = DockStyle.Fill,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var instructions = new Label
        {
            Text = Instructions + ".\nDepois digite aqui o código de 6 dígitos que aparece no tablet.",
            AutoSize = true,
            MaximumSize = new Size(D(440), 0),
            Margin = new Padding(D(3), D(3), D(3), D(12)),
        };
        layout.Controls.Add(instructions, 0, 0);
        layout.SetColumnSpan(instructions, 2);

        _code = new TextBox { Name = "Codigo", AccessibleName = "Código de pareamento", MaxLength = 6, Width = D(110) };
        _code.KeyPress += (_, e) => e.Handled = !char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar);
        layout.Controls.Add(new Label { Text = "Código de pareamento:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_code, 1, 1);

        _endpoint = new TextBox { Name = "Endereco", AccessibleName = "IP:porta (opcional)", Width = D(180) };
        layout.Controls.Add(new Label { Text = "IP:porta (opcional):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_endpoint, 1, 2);

        var hint = new Label
        {
            Text = "Deixe o IP:porta vazio para encontrar o tablet na rede automaticamente.",
            AutoSize = true,
            MaximumSize = new Size(D(440), 0),
            ForeColor = SystemColors.GrayText,
        };
        layout.Controls.Add(hint, 0, 3);
        layout.SetColumnSpan(hint, 2);

        _status = new Label { Name = "Status", AutoSize = true, MaximumSize = new Size(D(440), 0), Margin = new Padding(D(3), D(12), D(3), D(3)) };
        layout.Controls.Add(_status, 0, 4);
        layout.SetColumnSpan(_status, 2);

        _pair = new Button { Text = "Parear", AutoSize = true };
        _pair.Click += async (_, _) => await PairAsync();
        var close = new Button { Text = "Fechar", AutoSize = true };
        close.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, D(12), 0, 0),
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(_pair);
        layout.Controls.Add(buttons, 0, 5);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = _pair;
        CancelButton = close;
    }

    private int D(int logical) => LogicalToDeviceUnits(logical);

    private async Task PairAsync()
    {
        if (_busy)
        {
            return;
        }

        var code = _code.Text.Trim();
        if (!PairingInput.IsValidCode(code))
        {
            ShowStatus("O código deve ter exatamente 6 dígitos.", error: true);
            _code.Focus();
            return;
        }

        if (!PairingInput.TryParseEndpoint(_endpoint.Text, out var endpoint))
        {
            ShowStatus("Endereço inválido. Use IP:porta, por exemplo 192.168.0.45:37099, ou deixe vazio.", error: true);
            _endpoint.Focus();
            return;
        }

        SetBusy(true);
        ShowStatus(endpoint is null ? "Procurando o tablet na rede…" : "Pareando…", error: false);
        var progress = new Progress<string>(m =>
        {
            if (!IsDisposed)
            {
                ShowStatus(m, error: false);
            }
        });
        var adbPath = _adbPath;
        PairingResult result;
        try
        {
            result = await Task.Run(() => AdbPairing.Pair(adbPath, endpoint, code, ((IProgress<string>)progress).Report));
        }
        catch (Exception ex)
        {
            result = new PairingResult(false, $"Erro ao rodar o adb: {ex.Message.Trim()}");
        }

        if (IsDisposed)
        {
            if (result.Ok)
            {
                _onPaired();
            }

            return;
        }

        SetBusy(false);
        ShowStatus(result.Message, error: !result.Ok);
        if (result.Ok)
        {
            _code.Clear();
            _onPaired();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _pair.Enabled = !busy;
        _code.Enabled = !busy;
        _endpoint.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void ShowStatus(string text, bool error)
    {
        _status.Text = text;
        _status.ForeColor = error ? Color.Firebrick : SystemColors.ControlText;
    }
}
