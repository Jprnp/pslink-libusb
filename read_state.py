# -*- coding: utf-8 -*-
"""Primitiva de leitura robusta do Pulse Elite / dongle PS Link (VID 054C PID 0ECC).

Enumera a collection COL04 (UsagePage 0xFF01, Usage 0x20) por caps -- NAO por
instance path fixo, que muda a cada replug/reinstalacao (ver HANDOFF secao 2).
Faz polling de GET_REPORT(Feature 0xB0) e decodifica botoes/volume/mute.

Read-only: funciona mesmo com o app da Sony rodando (ver HANDOFF secao 3).
Uso:  py read_state.py            # 5 leituras espacadas + decodifica
      py read_state.py --watch    # loop continuo a ~5 Hz (Ctrl-C p/ sair)
"""
import ctypes, ctypes.wintypes as wt, sys, time

hid = ctypes.WinDLL("hid.dll")
setupapi = ctypes.WinDLL("setupapi.dll")
k32 = ctypes.WinDLL("kernel32.dll", use_last_error=True)


class GUID(ctypes.Structure):
    _fields_ = [("Data1", ctypes.c_ulong), ("Data2", ctypes.c_ushort),
                ("Data3", ctypes.c_ushort), ("Data4", ctypes.c_ubyte * 8)]


class SP_DEVICE_INTERFACE_DATA(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("InterfaceClassGuid", GUID),
                ("Flags", wt.DWORD), ("Reserved", ctypes.POINTER(ctypes.c_ulong))]


class SP_DEVICE_INTERFACE_DETAIL_DATA_W(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("DevicePath", ctypes.c_wchar * 1024)]


class HIDD_ATTRIBUTES(ctypes.Structure):
    _fields_ = [("Size", wt.ULONG), ("VendorID", ctypes.c_ushort),
                ("ProductID", ctypes.c_ushort), ("VersionNumber", ctypes.c_ushort)]


class HIDP_CAPS(ctypes.Structure):
    _fields_ = [("Usage", ctypes.c_ushort), ("UsagePage", ctypes.c_ushort),
                ("InputReportByteLength", ctypes.c_ushort),
                ("OutputReportByteLength", ctypes.c_ushort),
                ("FeatureReportByteLength", ctypes.c_ushort),
                ("Reserved", ctypes.c_ushort * 17),
                ("NumberLinkCollectionNodes", ctypes.c_ushort),
                ("NumberInputButtonCaps", ctypes.c_ushort),
                ("NumberInputValueCaps", ctypes.c_ushort),
                ("NumberInputDataIndices", ctypes.c_ushort),
                ("NumberOutputButtonCaps", ctypes.c_ushort),
                ("NumberOutputValueCaps", ctypes.c_ushort),
                ("NumberOutputDataIndices", ctypes.c_ushort),
                ("NumberFeatureButtonCaps", ctypes.c_ushort),
                ("NumberFeatureValueCaps", ctypes.c_ushort),
                ("NumberFeatureDataIndices", ctypes.c_ushort)]


GENERIC_READ = 0x80000000
FILE_SHARE_RW = 3
OPEN_EXISTING = 3
INVALID_HANDLE = ctypes.c_void_p(-1).value
DIGCF_PRESENT = 0x2
DIGCF_DEVICEINTERFACE = 0x10

k32.CreateFileW.restype = ctypes.c_void_p
k32.CreateFileW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p, wt.DWORD, wt.DWORD, ctypes.c_void_p]
k32.CloseHandle.argtypes = [ctypes.c_void_p]
setupapi.SetupDiGetClassDevsW.restype = ctypes.c_void_p
setupapi.SetupDiEnumDeviceInterfaces.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(GUID), wt.DWORD, ctypes.POINTER(SP_DEVICE_INTERFACE_DATA)]
setupapi.SetupDiGetDeviceInterfaceDetailW.argtypes = [ctypes.c_void_p, ctypes.POINTER(SP_DEVICE_INTERFACE_DATA), ctypes.c_void_p, wt.DWORD, ctypes.POINTER(wt.DWORD), ctypes.c_void_p]
setupapi.SetupDiDestroyDeviceInfoList.argtypes = [ctypes.c_void_p]
hid.HidP_GetCaps.argtypes = [ctypes.c_void_p, ctypes.POINTER(HIDP_CAPS)]
hid.HidD_GetPreparsedData.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
hid.HidD_FreePreparsedData.argtypes = [ctypes.c_void_p]
hid.HidD_GetAttributes.argtypes = [ctypes.c_void_p, ctypes.POINTER(HIDD_ATTRIBUTES)]
hid.HidD_GetFeature.argtypes = [ctypes.c_void_p, ctypes.c_void_p, wt.ULONG]


def find_col04():
    """Retorna (path, caps) da collection FF01/0x20 do dongle, ou (None, None)."""
    guid = GUID()
    hid.HidD_GetHidGuid(ctypes.byref(guid))
    hdev = setupapi.SetupDiGetClassDevsW(ctypes.byref(guid), None, None,
                                         DIGCF_PRESENT | DIGCF_DEVICEINTERFACE)
    found = (None, None)
    i = 0
    while True:
        ifd = SP_DEVICE_INTERFACE_DATA()
        ifd.cbSize = ctypes.sizeof(SP_DEVICE_INTERFACE_DATA)
        if not setupapi.SetupDiEnumDeviceInterfaces(hdev, None, ctypes.byref(guid), i, ctypes.byref(ifd)):
            break
        i += 1
        detail = SP_DEVICE_INTERFACE_DETAIL_DATA_W()
        detail.cbSize = 8 if ctypes.sizeof(ctypes.c_void_p) == 8 else 6
        req = wt.DWORD(0)
        setupapi.SetupDiGetDeviceInterfaceDetailW(hdev, ctypes.byref(ifd), ctypes.byref(detail),
                                                  ctypes.sizeof(detail), ctypes.byref(req), None)
        path = detail.DevicePath
        if "vid_054c&pid_0ecc" not in path.lower():
            continue
        h = k32.CreateFileW(path, GENERIC_READ, FILE_SHARE_RW, None, OPEN_EXISTING, 0, None)
        if h == INVALID_HANDLE or not h:
            continue
        pre = ctypes.c_void_p()
        if hid.HidD_GetPreparsedData(h, ctypes.byref(pre)):
            caps = HIDP_CAPS()
            hid.HidP_GetCaps(pre, ctypes.byref(caps))
            hid.HidD_FreePreparsedData(pre)
            if caps.UsagePage == 0xFF01 and caps.Usage == 0x20:
                found = (path, caps)
                k32.CloseHandle(h)
                break
        k32.CloseHandle(h)
    setupapi.SetupDiDestroyDeviceInfoList(hdev)
    return found


def open_col04(path):
    h = k32.CreateFileW(path, GENERIC_READ, FILE_SHARE_RW, None, OPEN_EXISTING, 0, None)
    if h == INVALID_HANDLE or not h:
        return None
    return h


def get_report(h, report_id, length=64):
    buf = ctypes.create_string_buffer(length)
    buf[0] = report_id
    ok = hid.HidD_GetFeature(h, buf, length)
    if not ok:
        return None, ctypes.get_last_error()
    return buf.raw, 0


def decode(raw):
    b39, b43, b44 = raw[39], raw[43], raw[44]
    btns = []
    if b39 & 0x08: btns.append("VOL+")
    if b39 & 0x10: btns.append("VOL-")
    if b39 & 0x20: btns.append("MUTE")
    return {
        "vol": b44,               # 0..13 (estado device-side)
        "vol_pct": round(b44 / 13 * 100),
        "mic_muted": b43 == 0x0C,  # 0xFC ativo / 0x0C mutado
        "buttons": btns or ["-"],
        "b39": b39, "b43": b43, "b44": b44,
    }


def main():
    watch = "--watch" in sys.argv
    path, caps = find_col04()
    if not path:
        print("COL04 NAO encontrada. Dongle plugado? MI_03 habilitado?")
        sys.exit(1)
    print("COL04:", path)
    print("caps: FeatureReportByteLength=%d InputReportByteLength=%d" %
          (caps.FeatureReportByteLength, caps.InputReportByteLength))
    h = open_col04(path)
    if not h:
        print("Falha ao abrir COL04, err=%d" % ctypes.get_last_error())
        sys.exit(1)

    if watch:
        print("watch @5Hz (Ctrl-C p/ sair). Aperte VOL+/VOL-/MUTE no headset...")
        last = None
        try:
            while True:
                raw, err = get_report(h, 0xB0)
                if raw is None:
                    print("GetFeature FAIL err=%d" % err)
                else:
                    d = decode(raw)
                    line = "vol=%2d/13 (%3d%%)  mic=%s  botoes=%s" % (
                        d["vol"], d["vol_pct"], "MUTADO" if d["mic_muted"] else "ativo",
                        "+".join(d["buttons"]))
                    if line != last:
                        print(line)
                        last = line
                time.sleep(0.2)
        except KeyboardInterrupt:
            pass
    else:
        for i in range(5):
            raw, err = get_report(h, 0xB0)
            if raw is None:
                print("#%d GetFeature(B0) FAIL err=%d" % (i + 1, err))
            else:
                d = decode(raw)
                print("#%d  %s  |  raw[39,43,44]=%02x %02x %02x" %
                      (i + 1, "vol=%2d/13 mic=%s botoes=%s" % (
                          d["vol"], "MUTADO" if d["mic_muted"] else "ativo",
                          "+".join(d["buttons"])), d["b39"], d["b43"], d["b44"]))
            time.sleep(0.3)
        raw82, err = get_report(h, 0x82)
        if raw82 is not None:
            print("report 0x82 (status/bateria?):", raw82[:12].hex(" "))
    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
