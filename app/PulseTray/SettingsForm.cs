using System.Drawing;

namespace PulseElite;

/// <summary>
/// Janela dedicada do app (abre ao clicar no ícone da tray). Sliders de sidetone e
/// volume, EQ, mute do mic, bateria e status ao vivo. Fecha escondendo (fica na tray).
///
/// Estado dos controles:
///   - Sidetone (slider)  : FUNCIONA (SetFeature 0xD0 máscara 0x40, validado).
///   - EQ (presets)       : FUNCIONA (0xD0 máscara 0x04).
///   - Volume (slider)    : desabilitado até a sondagem device-side (probe_volume_mic.py).
///   - Mic mute (toggle)  : desabilitado até a sondagem.
///   - Bateria            : desabilitado até decodificar o report 0x82.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly WinUsbDevice _dev;
    private readonly System.Windows.Forms.Timer _timer;

    private readonly Label _statusDot = new();
    private readonly Label _statusText = new();
    private readonly TrackBar _volume = new();
    private readonly Label _volumeVal = new();
    private readonly CheckBox _mic = new();
    private readonly TrackBar _sidetone = new();
    private readonly Label _sidetoneVal = new();
    private readonly ComboBox _eq = new();
    private readonly ProgressBar _battery = new();
    private readonly Label _batteryVal = new();

    private bool _userDraggingVolume;
    private bool _exiting;

    public SettingsForm(WinUsbDevice dev)
    {
        _dev = dev;

        Text = "Pulse Elite Companion";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 340);
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
        int y = 14;

        // --- status ---
        _statusDot.SetBounds(16, y, 14, 14);
        _statusDot.Text = "●"; // bolinha
        _statusText.SetBounds(34, y - 2, 300, 20);
        _statusText.Text = "…";
        Controls.Add(_statusDot); Controls.Add(_statusText);
        y += 34;

        // --- volume ---
        AddSectionLabel("Volume do headset", y); y += 22;
        _volume.SetBounds(16, y, 250, 40);
        _volume.Minimum = WinUsbDevice.VolMin; _volume.Maximum = WinUsbDevice.VolMax;
        _volume.TickFrequency = 1;
        _volume.MouseDown += (_, _) => _userDraggingVolume = true;
        _volume.MouseUp += (_, _) => _userDraggingVolume = false;
        _volume.Scroll += (_, _) => { _dev.SetVolume(_volume.Value); _volumeVal.Text = $"{_volume.Value}/15"; };
        _volumeVal.SetBounds(276, y + 8, 70, 20);
        Controls.Add(_volume); Controls.Add(_volumeVal);
        y += 44;

        // --- mic ---
        _mic.SetBounds(16, y, 300, 22);
        _mic.Text = "Microfone ativo";
        // Click só dispara por interação do usuário (o timer mexe em .Checked sem disparar Click),
        // então não há loop de feedback. Ao clicar, .Checked já reflete o novo estado.
        _mic.Click += (_, _) => _dev.SetMicMuted(!_mic.Checked);
        Controls.Add(_mic);
        y += 32;

        // --- sidetone (FUNCIONA) ---
        AddSectionLabel("Sidetone (retorno do mic)", y); y += 22;
        _sidetone.SetBounds(16, y, 250, 40);
        _sidetone.Minimum = WinUsbDevice.SidetoneMin; _sidetone.Maximum = WinUsbDevice.SidetoneMax;
        _sidetone.TickFrequency = 1;
        _sidetone.Scroll += (_, _) =>
        {
            _dev.SetSidetone(_sidetone.Value);
            _sidetoneVal.Text = _sidetone.Value == 0 ? "off" : _sidetone.Value.ToString();
        };
        _sidetoneVal.SetBounds(276, y + 8, 70, 20);
        _sidetoneVal.Text = "off";
        Controls.Add(_sidetone); Controls.Add(_sidetoneVal);
        y += 44;

        // --- EQ (FUNCIONA) ---
        AddSectionLabel("Equalizador", y); y += 22;
        _eq.SetBounds(16, y, 160, 24);
        _eq.DropDownStyle = ComboBoxStyle.DropDownList;
        _eq.Items.AddRange(new object[] { "Preset 1", "Preset 2", "Preset 3" });
        _eq.SelectedIndexChanged += (_, _) =>
        {
            if (_eq.SelectedIndex >= 0) _dev.SetEq(WinUsbDevice.EqPresets[_eq.SelectedIndex]);
        };
        Controls.Add(_eq);
        y += 34;

        // --- bateria (placeholder) ---
        AddSectionLabel("Bateria", y); y += 22;
        _battery.SetBounds(16, y, 250, 16);
        _battery.Minimum = 0; _battery.Maximum = 100;
        _batteryVal.SetBounds(276, y - 2, 70, 20);
        _batteryVal.Text = "—";
        Controls.Add(_battery); Controls.Add(_batteryVal);
    }

    private void AddSectionLabel(string text, int y)
    {
        var l = new Label
        {
            Text = text,
            ForeColor = Color.FromArgb(114, 137, 218),
            Font = new Font("Segoe UI", 8.25f, FontStyle.Bold),
        };
        l.SetBounds(16, y, 320, 16);
        Controls.Add(l);
    }

    private void RefreshFromState()
    {
        var st = _dev.State;
        _statusDot.ForeColor = st.Connected ? Color.FromArgb(46, 204, 113) : Color.FromArgb(231, 76, 60);
        _statusText.Text = st.Connected ? "Conectado" : "Desconectado";

        if (st.Connected)
        {
            if (!_userDraggingVolume) _volume.Value = Math.Clamp(st.Volume, _volume.Minimum, _volume.Maximum);
            _volumeVal.Text = $"{st.Volume}/15";
            _mic.Text = st.MicMuted ? "Microfone mudo" : "Microfone ativo";
            _mic.Checked = !st.MicMuted;
        }
        else
        {
            _volumeVal.Text = "—";
        }

        int bat = _dev.BatteryPercent;
        if (bat >= 0) { _battery.Value = bat; _batteryVal.Text = $"{bat}%"; }
        else { _batteryVal.Text = "—"; }
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
