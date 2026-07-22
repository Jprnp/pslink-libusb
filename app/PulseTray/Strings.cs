using Microsoft.Win32;

namespace PulseElite;

public enum Lang { En, Pt, Es }

/// <summary>
/// Localização leve. Inglês é o padrão; Português e Espanhol são opcionais e
/// selecionáveis em runtime (persistido no registro). T(key) devolve a string
/// do idioma atual, com fallback pro inglês e, por fim, pra própria chave.
/// </summary>
public static class Strings
{
    private const string RegKey = @"Software\PulseEliteCompanion";
    private const string RegVal = "Lang";

    public static Lang Current { get; private set; } = LoadSaved();

    // chave -> [En, Pt, Es]
    private static readonly Dictionary<string, string[]> M = new()
    {
        ["connected"]    = new[] { "Connected", "Conectado", "Conectado" },
        ["disconnected"] = new[] { "Disconnected", "Desconectado", "Desconectado" },
        ["battery"]      = new[] { "Battery", "Bateria", "Batería" },
        ["volume"]       = new[] { "Headset volume", "Volume do headset", "Volumen del headset" },
        ["mic_on"]       = new[] { "Microphone on", "Microfone ativo", "Micrófono activo" },
        ["mic_muted"]    = new[] { "Microphone muted", "Microfone mudo", "Micrófono silenciado" },
        ["sidetone"]     = new[] { "Sidetone (mic monitoring)", "Sidetone (retorno do mic)", "Sidetone (retorno del mic)" },
        ["equalizer"]    = new[] { "Equalizer", "Equalizador", "Ecualizador" },
        ["off"]          = new[] { "off", "off", "off" },
        ["st_off"]       = new[] { "Off", "Desligado", "Apagado" },
        ["st_low"]       = new[] { "Low", "Baixo", "Bajo" },
        ["st_medium"]    = new[] { "Medium", "Médio", "Medio" },
        ["st_high"]      = new[] { "High", "Alto", "Alto" },
        ["st_max"]       = new[] { "Max", "Máximo", "Máximo" },
        ["open_panel"]   = new[] { "Open panel…", "Abrir painel…", "Abrir panel…" },
        ["autostart"]    = new[] { "Start with Windows", "Iniciar com o Windows", "Iniciar con Windows" },
        ["exit"]         = new[] { "Exit", "Sair", "Salir" },
        ["language"]     = new[] { "Language", "Idioma", "Idioma" },
        ["tt_on"]        = new[] { "on", "ativo", "activo" },
        ["tt_muted"]     = new[] { "muted", "mudo", "silenc." },
        ["tt_disc"]      = new[] { "disconnected", "desconectado", "desconectado" },
    };

    public static string T(string key)
        => M.TryGetValue(key, out var v) ? v[(int)Current] : key;

    public static void Set(Lang lang)
    {
        Current = lang;
        try { using var k = Registry.CurrentUser.CreateSubKey(RegKey); k.SetValue(RegVal, lang.ToString()); }
        catch { /* sem persistência não é fatal */ }
    }

    private static Lang LoadSaved()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RegKey);
            if (k?.GetValue(RegVal) is string s && Enum.TryParse<Lang>(s, out var l)) return l;
        }
        catch { }
        return Lang.En; // padrão: inglês
    }
}
