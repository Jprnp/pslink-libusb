using System.Drawing;
using Microsoft.Win32;

namespace PulseElite;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var app = new TrayApp();
        Application.Run();
    }
}

sealed class TrayApp : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "PulseEliteCompanion";

    private readonly WinUsbDevice _dev = new();
    private readonly SettingsForm _form;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly Icon _iconOn, _iconOff;

    private int _sidetone;          // último nível setado (não há read-back)
    private int _eqIndex;           // último preset setado

    public TrayApp()
    {
        _iconOn = LoadIcon("icon.ico");       // colorido = conectado
        _iconOff = LoadIcon("icon_off.ico");  // cinza = sem device

        _form = new SettingsForm(_dev) { Icon = _iconOn };

        _tray = new NotifyIcon
        {
            Icon = _iconOff,
            Visible = true,
            Text = "Pulse Elite — iniciando…",
            ContextMenuStrip = BuildMenu(),
        };
        // clique esquerdo no ícone abre o painel dedicado
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _form.ShowPanel(); };

        _uiTimer = new System.Windows.Forms.Timer { Interval = 750 };
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();

        _dev.Start();
    }

    // ---------------- menu ----------------
    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var title = new ToolStripMenuItem("Pulse Elite Companion") { Enabled = false };
        var status = new ToolStripMenuItem("…") { Enabled = false, Name = "status" };

        // Sidetone
        var side = new ToolStripMenuItem("Sidetone");
        foreach (var (label, lvl) in new (string, int)[] { ("Desligado", 0), ("Baixo", 4), ("Médio", 8), ("Alto", 12), ("Máximo", 15) })
        {
            var item = new ToolStripMenuItem(label) { Tag = lvl };
            item.Click += (s, _) =>
            {
                int v = (int)((ToolStripMenuItem)s!).Tag!;
                if (_dev.SetSidetone(v)) _sidetone = v;
                MarkChecked(side, v);
            };
            side.DropDownItems.Add(item);
        }

        // EQ
        var eq = new ToolStripMenuItem("Equalizador");
        for (int i = 0; i < WinUsbDevice.EqPresets.Length; i++)
        {
            int idx = i;
            var item = new ToolStripMenuItem($"Preset {i + 1}") { Tag = idx };
            item.Click += (s, _) =>
            {
                if (_dev.SetEq(WinUsbDevice.EqPresets[idx])) _eqIndex = idx;
                MarkChecked(eq, idx);
            };
            eq.DropDownItems.Add(item);
        }

        var autostart = new ToolStripMenuItem("Iniciar com o Windows") { CheckOnClick = true, Checked = IsAutostart() };
        autostart.Click += (s, _) => SetAutostart(((ToolStripMenuItem)s!).Checked);

        var open = new ToolStripMenuItem("Abrir painel…");
        open.Click += (_, _) => _form.ShowPanel();

        var exit = new ToolStripMenuItem("Sair");
        exit.Click += (_, _) => { Dispose(); Application.Exit(); };

        menu.Items.AddRange(new ToolStripItem[]
        {
            title, status, new ToolStripSeparator(),
            open, side, eq, new ToolStripSeparator(),
            autostart, new ToolStripSeparator(), exit,
        });

        // atualiza os checks/status ao abrir o menu
        menu.Opening += (_, _) =>
        {
            status.Text = StatusLine();
            MarkChecked(side, _sidetone);
            MarkChecked(eq, _eqIndex);
        };
        return menu;
    }

    private static void MarkChecked(ToolStripMenuItem parent, int tagValue)
    {
        foreach (ToolStripMenuItem it in parent.DropDownItems)
            it.Checked = it.Tag is int t && t == tagValue;
    }

    // ---------------- UI refresh ----------------
    private void RefreshUi()
    {
        var st = _dev.State;
        _tray.Icon = st.Connected ? _iconOn : _iconOff;
        _tray.Text = Truncate(StatusLine(), 63); // tooltip do tray tem limite de 63 chars
    }

    private string StatusLine()
    {
        var st = _dev.State;
        if (!st.Connected) return "Pulse Elite — desconectado";
        string mic = st.MicMuted ? "mudo" : "ativo";
        return $"Pulse Elite • Volume {st.Volume}/15 • Mic {mic}";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // ---------------- autostart (HKCU Run) ----------------
    private static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) is not null;
    }

    private static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                     ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunValue, $"\"{Application.ExecutablePath}\"");
        else k.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    // ---------------- ícone (recurso .ico embutido) ----------------
    private static Icon LoadIcon(string name)
    {
        using var s = System.Reflection.Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("PulseElite." + name)
            ?? throw new InvalidOperationException("recurso de ícone não encontrado: " + name);
        return new Icon(s);
    }

    public void Dispose()
    {
        _uiTimer.Stop();
        _form.ForceClose();
        _tray.Visible = false;
        _tray.Dispose();
        _dev.Dispose();
        _iconOn.Dispose();
        _iconOff.Dispose();
    }
}
