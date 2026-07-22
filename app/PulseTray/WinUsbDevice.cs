using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PulseElite;

/// <summary>
/// Camada de device do Pulse Elite / dongle PS Link (VID 054C PID 0ECC) via WinUSB.
///
/// Porte 1:1 do protótipo Python validado ao vivo (ver HANDOFF §0):
///   - keepalive/presença: GET_REPORT(Feature 0xB0) a ~5 Hz SÓ no EP0 (control transfer);
///     o pipe interrupt 0x81 NUNCA é aberto -> sem babble -> sem freeze.
///   - leitura: volume (byte 44, 0-15), mute do mic (byte 43), botões (byte 39).
///   - escrita: sidetone (SET_REPORT 0xD0 máscara 0x40) e EQ (máscara 0x04).
///   - reconexão automática em replug/remoção.
///
/// Pré-requisito: MI_03 rebindado pra WinUSB. GUID de interface abaixo = o que o Zadig
/// atribuiu no protótipo; o app final trará INF próprio com um GUID nosso (trocar aqui).
/// </summary>
public sealed class WinUsbDevice : IDisposable
{
    // Identidade do device: constante pra TODOS os dongles PS Link (qualquer unidade, qualquer PC).
    private const string HardwareIdMatch = "VID_054C&PID_0ECC";
    private const string InterfaceMatch = "MI_03";
    // GUID de interface canônico do nosso INF (app/driver/PulseElite.inf) — igual em toda máquina
    // que instalar via install.ps1. Só é usado como FALLBACK: o caminho normal descobre o GUID
    // em runtime lendo o registro do device, então funciona também com bind via Zadig (GUID aleatório).
    private static readonly Guid FallbackGuid = new("E5B7A2D4-3C61-4F8B-9A2E-7D1C6F80B4A3");

    // HID-over-control (bytes validados na captura; HANDOFF §3-5, §8.4)
    private const byte GetBmReq = 0xA1, GetReq = 0x01;   // class/interface IN  -> GET_REPORT
    private const byte SetBmReq = 0x21, SetReq = 0x09;   // class/interface OUT -> SET_REPORT
    private const ushort FeatureType = 0x03;             // byte alto do wValue
    private const ushort IfaceIndex = 3;                 // wIndex = interface (MI_03)

    public const int VolMin = 0, VolMax = 15;            // byte 44 (corrigido: era 0-13)
    public const int SidetoneMin = 0, SidetoneMax = 15;  // faixa da UI da Sony
    public static readonly byte[] EqPresets = { 0x00, 0x20, 0x40 };

    private const int PollIntervalMs = 200;              // 5 Hz — é o keepalive de presença, não latência de botão
    private const int FailsBeforeReconnect = 10;         // ~2 s sem resposta -> reenumera

    private SafeFileHandle? _fileHandle;
    private IntPtr _winusb = IntPtr.Zero;
    private readonly object _io = new();                 // serializa control transfers (poll vs escrita)
    private Thread? _thread;
    private volatile bool _running;

    public PulseState State { get; private set; } = PulseState.Disconnected;
    public bool Connected => State.Connected;
    public int BatteryPercent { get; private set; } = -1;   // -1 = desconhecido; via report 0x82 (byte3/15)

    /// <summary>Disparado quando o estado LÓGICO muda (vol/mic/botão) ou a presença sobe/cai.</summary>
    public event Action<PulseState>? Changed;

    // ---------------- ciclo de vida ----------------
    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "pulse-keepalive" };
        _thread.Start();
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(2));
        CloseDevice();
    }

    // ---------------- descoberta / abertura ----------------
    private bool OpenDevice()
    {
        string? path = FindDevicePath();
        if (path is null) return false;

        var handle = Native.CreateFile(path,
            Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING,
            Native.FILE_ATTRIBUTE_NORMAL | Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid) return false;

        if (!Native.WinUsb_Initialize(handle, out IntPtr winusb))
        {
            handle.Dispose();
            return false;
        }

        _fileHandle = handle;
        _winusb = winusb;

        // porteiro: MI_03 precisa estar no WinUSB (senão o GET_REPORT falha)
        if (TryGetReport(0xB0, out _)) return true;

        CloseDevice();
        return false;
    }

    private void CloseDevice()
    {
        lock (_io)
        {
            if (_winusb != IntPtr.Zero) { Native.WinUsb_Free(_winusb); _winusb = IntPtr.Zero; }
            _fileHandle?.Dispose();
            _fileHandle = null;
        }
    }

    private static string? FindDevicePath()
    {
        // GUID descoberto do próprio device (qualquer bind/máquina); fallback = GUID conhecido.
        Guid guid = DiscoverInterfaceGuid() ?? FallbackGuid;
        IntPtr set = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero,
            Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == Native.INVALID_HANDLE_VALUE) return null;
        try
        {
            var ifd = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifd); i++)
            {
                Native.SetupDiGetDeviceInterfaceDetail(set, ref ifd, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                if (required == 0) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    // cbSize do header: 8 em x64, 6 em x86
                    Marshal.WriteInt32(detail, Environment.Is64BitProcess ? 8 : 6);
                    if (Native.SetupDiGetDeviceInterfaceDetail(set, ref ifd, detail, required, out _, IntPtr.Zero))
                    {
                        string? path = Marshal.PtrToStringUni(detail + 4);
                        // confere que é o nosso device (VID/PID) — segurança se o GUID for compartilhado
                        if (path != null && path.Contains(HardwareIdMatch, StringComparison.OrdinalIgnoreCase))
                            return path;
                    }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return null;
    }

    /// <summary>
    /// Descobre o GUID de interface do MI_03 em runtime: varre os devices USB presentes,
    /// acha o que casa VID_054C&amp;PID_0ECC&amp;MI_03 pelo hardware-id e lê o DeviceInterfaceGUID(s)
    /// do registro do device. Independe de qual INF/Zadig fez o bind -> portável.
    /// </summary>
    private static Guid? DiscoverInterfaceGuid()
    {
        IntPtr set = Native.SetupDiGetClassDevsAll(IntPtr.Zero, "USB", IntPtr.Zero,
            Native.DIGCF_PRESENT | Native.DIGCF_ALLCLASSES);
        if (set == Native.INVALID_HANDLE_VALUE) return null;
        try
        {
            var did = new Native.SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVINFO_DATA>() };
            for (uint i = 0; Native.SetupDiEnumDeviceInfo(set, i, ref did); i++)
            {
                string hw = GetHardwareIds(set, ref did);
                if (hw.Contains(HardwareIdMatch, StringComparison.OrdinalIgnoreCase) &&
                    hw.Contains(InterfaceMatch, StringComparison.OrdinalIgnoreCase))
                {
                    Guid? g = ReadInterfaceGuid(set, ref did);
                    if (g != null) return g;
                }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return null;
    }

    private static string GetHardwareIds(IntPtr set, ref Native.SP_DEVINFO_DATA did)
    {
        Native.SetupDiGetDeviceRegistryProperty(set, ref did, Native.SPDRP_HARDWAREID, out _, null, 0, out uint req);
        if (req == 0) return "";
        var buf = new byte[req];
        if (!Native.SetupDiGetDeviceRegistryProperty(set, ref did, Native.SPDRP_HARDWAREID, out _, buf, req, out _))
            return "";
        return System.Text.Encoding.Unicode.GetString(buf); // REG_MULTI_SZ (nulls embutidos; Contains funciona)
    }

    private static Guid? ReadInterfaceGuid(IntPtr set, ref Native.SP_DEVINFO_DATA did)
    {
        IntPtr hkey = Native.SetupDiOpenDevRegKey(set, ref did, Native.DICS_FLAG_GLOBAL, 0,
            Native.DIREG_DEV, Native.KEY_READ);
        if (hkey == Native.INVALID_HANDLE_VALUE) return null;
        try
        {
            foreach (var name in new[] { "DeviceInterfaceGUIDs", "DeviceInterfaceGUID" })
            {
                uint size = 0;
                if (Native.RegQueryValueEx(hkey, name, IntPtr.Zero, out _, null, ref size) != 0 || size == 0)
                    continue;
                var buf = new byte[size];
                if (Native.RegQueryValueEx(hkey, name, IntPtr.Zero, out _, buf, ref size) != 0)
                    continue;
                foreach (var part in System.Text.Encoding.Unicode.GetString(buf).Split('\0'))
                    if (Guid.TryParse(part.Trim(), out Guid g)) return g;
            }
        }
        finally { Native.RegCloseKey(hkey); }
        return null;
    }

    // ---------------- transfers ----------------
    private static ushort WValue(byte reportId) => (ushort)((FeatureType << 8) | reportId);

    private bool TryGetReport(byte reportId, out byte[] buffer)
    {
        buffer = new byte[64];
        var pkt = new Native.WINUSB_SETUP_PACKET
        {
            RequestType = GetBmReq,
            Request = GetReq,
            Value = WValue(reportId),
            Index = IfaceIndex,
            Length = (ushort)buffer.Length,
        };
        lock (_io)
        {
            if (_winusb == IntPtr.Zero) return false;
            return Native.WinUsb_ControlTransfer(_winusb, pkt, buffer, (uint)buffer.Length, out _, IntPtr.Zero);
        }
    }

    private bool TrySetReport(byte[] data)
    {
        var pkt = new Native.WINUSB_SETUP_PACKET
        {
            RequestType = SetBmReq,
            Request = SetReq,
            Value = WValue(data[0]),
            Index = IfaceIndex,
            Length = (ushort)data.Length,
        };
        lock (_io)
        {
            if (_winusb == IntPtr.Zero) return false;
            return Native.WinUsb_ControlTransfer(_winusb, pkt, data, (uint)data.Length, out _, IntPtr.Zero);
        }
    }

    // ---------------- escrita pública ----------------
    public bool SetSidetone(int level)
    {
        level = Math.Clamp(level, SidetoneMin, SidetoneMax);
        var b = new byte[22]; b[0] = 0xD0; b[1] = 0x40; b[21] = (byte)level;
        return TrySetReport(b);
    }

    public bool SetEq(byte preset)
    {
        var b = new byte[22]; b[0] = 0xD0; b[1] = 0x04; b[2] = preset;
        return TrySetReport(b);
    }

    /// <summary>
    /// Volume device-side (aplicado no DSP do headset). Descoberto por sondagem 2026-07-22:
    /// SET_REPORT 0xD0 máscara 0x02, valor no byte 2 num campo de 5 bits (0-31);
    /// o byte 44 lido de volta = (valor &amp; 0x1F) >> 1. Para o nível L (0-15): valor = L*2.
    /// </summary>
    public bool SetVolume(int level)
    {
        level = Math.Clamp(level, VolMin, VolMax);
        var b = new byte[22]; b[0] = 0xD0; b[1] = 0x02; b[2] = (byte)(level * 2);
        return TrySetReport(b);
    }

    /// <summary>
    /// Mute do microfone device-side. Descoberto por captura do app da Sony (2026-07-22):
    /// SET_REPORT 0xD0 máscara 0x01, byte[2] = 1 (mutar) / 0 (desmutar).
    /// </summary>
    public bool SetMicMuted(bool muted)
    {
        var b = new byte[22]; b[0] = 0xD0; b[1] = 0x01; b[2] = (byte)(muted ? 1 : 0);
        return TrySetReport(b);
    }

    // ---------------- loop de keepalive ----------------
    private void RunLoop()
    {
        int fails = 0, battTick = 0;
        while (_running)
        {
            if (_winusb == IntPtr.Zero)
            {
                if (!OpenDevice()) { UpdateState(PulseState.Disconnected); BatteryPercent = -1; Thread.Sleep(500); continue; }
                fails = 0;
            }
            if (TryGetReport(0xB0, out byte[] raw))
            {
                fails = 0;
                UpdateState(PulseState.FromReport(raw));
            }
            else if (++fails >= FailsBeforeReconnect)
            {
                CloseDevice();
                UpdateState(PulseState.Disconnected);
                BatteryPercent = -1;
                fails = 0;
            }
            // bateria: report 0x82, byte[3] numa escala 0-15 (a cada ~4 s, não é urgente)
            if (--battTick <= 0)
            {
                battTick = 20;
                if (TryGetReport(0x82, out byte[] b82))
                    BatteryPercent = Math.Clamp((int)Math.Round(b82[3] / 15.0 * 100), 0, 100);
            }
            Thread.Sleep(PollIntervalMs);
        }
    }

    private void UpdateState(PulseState next)
    {
        if (State.Key == next.Key) return;
        State = next;
        Changed?.Invoke(next);
    }
}

/// <summary>Snapshot imutável do estado lido do headset.</summary>
public readonly struct PulseState
{
    public bool Connected { get; }
    public int Volume { get; }        // 0-15
    public bool MicMuted { get; }
    public bool VolUp { get; }
    public bool VolDown { get; }
    public bool MuteBtn { get; }

    public static readonly PulseState Disconnected = new();

    private PulseState(byte[] raw)
    {
        byte b = raw[39];
        // bit0 do byte39 = headset linkado/ligado (achado 2026-07-22: 0x01 ligado, 0x00 desligado).
        // O dongle responde ao poll mesmo com o headset off, então este bit é o link real.
        Connected = (b & 0x01) != 0;
        Volume = raw[44];
        MicMuted = (raw[43] & 0xF0) == 0x00;  // 0xF*=ativo, 0x0*=mudo (nibble alto)
        VolUp = (b & 0x08) != 0;
        VolDown = (b & 0x10) != 0;
        MuteBtn = (b & 0x20) != 0;
    }

    public static PulseState FromReport(byte[] raw) => new(raw);

    /// <summary>Chave lógica p/ detectar mudança (ignora bytes estáticos/ruído).</summary>
    public (bool, int, bool, bool, bool, bool) Key => (Connected, Volume, MicMuted, VolUp, VolDown, MuteBtn);
}
