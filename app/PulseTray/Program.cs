using System.Drawing;
using Microsoft.Win32;

namespace PulseElite;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var app = new TrayApp(args.Contains("--panel"));
        Application.Run();
    }
}

sealed class TrayApp : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "PulseEliteCompanion";

    private readonly WinUsbDevice _dev = new();
    private SettingsForm _form;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly Icon _iconOn, _iconOff;

    public TrayApp(bool openPanel = false)
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

        if (openPanel) _form.ShowPanel();
    }

    // ---------------- menu ----------------
    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var title = new ToolStripMenuItem("Pulse Elite Companion") { Enabled = false };
        var status = new ToolStripMenuItem("…") { Enabled = false, Name = "status" };

        // Sidetone e EQ ficam só no painel dedicado (menu da tray é enxuto).

        var autostart = new ToolStripMenuItem(Strings.T("autostart")) { CheckOnClick = true, Checked = IsAutostart() };
        autostart.Click += (s, _) => SetAutostart(((ToolStripMenuItem)s!).Checked);

        var open = new ToolStripMenuItem(Strings.T("open_panel"));
        open.Click += (_, _) => _form.ShowPanel();

        // idioma: inglês padrão + PT/ES (troca em runtime)
        var lang = new ToolStripMenuItem(Strings.T("language"));
        foreach (var (name, l) in new (string, Lang)[] { ("English", Lang.En), ("Português", Lang.Pt), ("Español", Lang.Es) })
        {
            var li = new ToolStripMenuItem(name) { Tag = l, Checked = Strings.Current == l };
            li.Click += (_, _) => { Strings.Set(l); RebuildUi(); };
            lang.DropDownItems.Add(li);
        }

        var exit = new ToolStripMenuItem(Strings.T("exit"));
        exit.Click += (_, _) => { Dispose(); Application.Exit(); };

        menu.Items.AddRange(new ToolStripItem[]
        {
            title, status, new ToolStripSeparator(),
            open, new ToolStripSeparator(),
            autostart, lang, new ToolStripSeparator(), exit,
        });

        menu.Opening += (_, _) => status.Text = StatusLine();
        return menu;
    }

    // recria menu e painel após troca de idioma (as strings são lidas na construção)
    private void RebuildUi()
    {
        bool wasVisible = _form.Visible;
        var oldMenu = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = BuildMenu();
        oldMenu?.Dispose();
        _form.ForceClose();
        _form.Dispose();
        _form = new SettingsForm(_dev) { Icon = _iconOn };
        if (wasVisible) _form.ShowPanel();
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
        if (!st.Connected) return $"Pulse Elite — {Strings.T("tt_disc")}";
        string mic = st.MicMuted ? Strings.T("tt_muted") : Strings.T("tt_on");
        int bat = _dev.BatteryPercent;
        string batTxt = bat >= 0 ? $" • Bat {bat}%" : "";
        return $"Pulse Elite • Vol {st.Volume}/15 • Mic {mic}{batTxt}";
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
