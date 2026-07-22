using System.Drawing;

namespace PulseElite;

/// <summary>
/// Janela dedicada do app (abre ao clicar no ícone da tray). Bateria no topo, depois
/// volume, mic, sidetone e EQ. Fecha escondendo (fica na tray).
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly WinUsbDevice _dev;
    private readonly System.Windows.Forms.Timer _timer;

    private readonly Label _statusDot = new();
    private readonly Label _statusText = new();
    private readonly ProgressBar _battery = new();
    private readonly Label _batteryVal = new();
    private readonly TrackBar _volume = new();
    private readonly Label _volumeVal = new();
    private readonly CheckBox _mic = new();
    private readonly TrackBar _sidetone = new();
    private readonly Label _sidetoneVal = new();
    private readonly ComboBox _eq = new();

    private bool _userDraggingVolume;
    private bool _exiting;

    // layout
    private const int LEFT = 20;
    private const int CONTENT_W = 300;
    private const int VAL_X = LEFT + CONTENT_W + 8;
    private const int VAL_W = 66;
    private static readonly Color Accent = Color.FromArgb(114, 137, 218);

    public SettingsForm(WinUsbDevice dev)
    {
        _dev = dev;

        Text = "Pulse Elite Companion";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(VAL_X + VAL_W + LEFT, 356);
        BackColor = Color.FromArgb(32, 34, 37);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9f);

        BuildLayout();

        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) => RefreshFromState();
        _timer.Start();
    }

    private void BuildLayout()
    {
        // --- status (topo) ---
        _statusDot.AutoSize = false;
        _statusDot.SetBounds(LEFT, 16, 14, 16);
        _statusDot.Font = new Font("Segoe UI", 11f);
        _statusDot.Text = "●";
        _statusText.SetBounds(LEFT + 18, 16, 260, 20);
        _statusText.Text = "…";
        Controls.Add(_statusDot);
        Controls.Add(_statusText);

        // --- bateria (logo abaixo do status, conforme pedido) ---
        SectionLabel(Strings.T("battery"), 48);
        _battery.SetBounds(LEFT, 70, CONTENT_W, 16);
        _battery.Minimum = 0; _battery.Maximum = 100;
        ValueLabel(_batteryVal, 68, "—");
        Controls.Add(_battery);

        // --- volume ---
        SectionLabel(Strings.T("volume"), 100);
        Slider(_volume, WinUsbDevice.VolMin, WinUsbDevice.VolMax, 122);
        _volume.MouseDown += (_, _) => _userDraggingVolume = true;
        _volume.MouseUp += (_, _) => _userDraggingVolume = false;
        _volume.Scroll += (_, _) => { _dev.SetVolume(_volume.Value); _volumeVal.Text = $"{_volume.Value}/15"; };
        ValueLabel(_volumeVal, 126, "—");

        // --- mic ---
        _mic.SetBounds(LEFT, 166, CONTENT_W, 24);
        _mic.Text = Strings.T("mic_on");
        // Click só dispara por interação do usuário (o timer mexe em .Checked sem disparar Click).
        _mic.Click += (_, _) => _dev.SetMicMuted(!_mic.Checked);
        Controls.Add(_mic);

        // --- sidetone ---
        SectionLabel(Strings.T("sidetone"), 200);
        Slider(_sidetone, WinUsbDevice.SidetoneMin, WinUsbDevice.SidetoneMax, 222);
        _sidetone.Scroll += (_, _) =>
        {
            _dev.SetSidetone(_sidetone.Value);
            _sidetoneVal.Text = _sidetone.Value == 0 ? Strings.T("off") : _sidetone.Value.ToString();
        };
        ValueLabel(_sidetoneVal, 226, Strings.T("off"));

        // --- EQ ---
        SectionLabel(Strings.T("equalizer"), 268);
        _eq.SetBounds(LEFT, 290, 180, 24);
        _eq.DropDownStyle = ComboBoxStyle.DropDownList;
        _eq.FlatStyle = FlatStyle.Flat;
        _eq.Items.AddRange(new object[] { "Preset 1", "Preset 2", "Preset 3" });
        _eq.SelectedIndexChanged += (_, _) =>
        {
            if (_eq.SelectedIndex >= 0) _dev.SetEq(WinUsbDevice.EqPresets[_eq.SelectedIndex]);
        };
        Controls.Add(_eq);
    }

    private void SectionLabel(string text, int y)
    {
        var l = new Label
        {
            Text = text,
            ForeColor = Accent,
            Font = new Font("Segoe UI", 8.25f, FontStyle.Bold),
            AutoSize = false,
        };
        l.SetBounds(LEFT, y, CONTENT_W, 16);
        Controls.Add(l);
    }

    private void ValueLabel(Label lbl, int y, string init)
    {
        lbl.SetBounds(VAL_X, y, VAL_W, 20);
        lbl.TextAlign = ContentAlignment.MiddleLeft;
        lbl.Text = init;
        Controls.Add(lbl);
    }

    private void Slider(TrackBar tb, int min, int max, int y)
    {
        tb.AutoSize = false;
        tb.SetBounds(LEFT - 4, y, CONTENT_W, 34);
        tb.Minimum = min; tb.Maximum = max;
        tb.TickStyle = TickStyle.None;
        Controls.Add(tb);
    }

    private void RefreshFromState()
    {
        var st = _dev.State;
        _statusDot.ForeColor = st.Connected ? Color.FromArgb(46, 204, 113) : Color.FromArgb(231, 76, 60);
        _statusText.Text = st.Connected ? Strings.T("connected") : Strings.T("disconnected");

        if (st.Connected)
        {
            if (!_userDraggingVolume) _volume.Value = Math.Clamp(st.Volume, _volume.Minimum, _volume.Maximum);
            _volumeVal.Text = $"{st.Volume}/15";
            _mic.Text = st.MicMuted ? Strings.T("mic_muted") : Strings.T("mic_on");
            _mic.Checked = !st.MicMuted;
        }
        else
        {
            _volumeVal.Text = "—";
        }

        int bat = _dev.BatteryPercent;
        if (st.Connected && bat >= 0) { _battery.Value = bat; _batteryVal.Text = $"{bat}%"; }
        else { _battery.Value = 0; _batteryVal.Text = "—"; }
    }

    public void ShowPanel()
    {
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    public void ForceClose() { _exiting = true; Close(); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;   // não fecha o app; só esconde na tray
            Hide();
            return;
        }
        _timer.Stop();
        base.OnFormClosing(e);
    }
}
