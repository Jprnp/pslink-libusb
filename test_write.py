# -*- coding: utf-8 -*-
"""Testa a primitiva de ESCRITA (HidD_SetFeature 0xD0) na COL04 do dongle PS Link.

Objetivo: provar o que o HANDOFF secao 5 marca como "inferencia forte" -- que a
COL04 abre RW e aceita SetFeature -- assim que o handle da Sony for liberado
(app fechado). Testavel SEM rebind WinUSB e SEM remover a Sony (basta matar o
processo "PlayStation Link.exe" antes de rodar; reversivel reiniciando-o).

Bytes do sidetone/EQ vem da captura ao vivo (HANDOFF secao 5), nao sao chute.

Uso:
  py test_write.py                 # so tenta abrir RW e reporta (nao escreve)
  py test_write.py --sidetone N    # escreve sidetone nivel N (0-15) e le de volta
  py test_write.py --eq P          # escreve EQ preset P (0, 32 ou 64) e le de volta
"""
import ctypes, ctypes.wintypes as wt, sys, time
import read_state as rs  # reaproveita find_col04 / decode

GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
FILE_SHARE_RW = 3
OPEN_EXISTING = 3
INVALID_HANDLE = ctypes.c_void_p(-1).value

hid = rs.hid
k32 = rs.k32
hid.HidD_SetFeature.argtypes = [ctypes.c_void_p, ctypes.c_void_p, wt.ULONG]


def open_rw(path):
    """Abre COL04 com GENERIC_READ|GENERIC_WRITE. Retorna (handle, err)."""
    h = k32.CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                        None, OPEN_EXISTING, 0, None)
    if h == INVALID_HANDLE or not h:
        return None, ctypes.get_last_error()
    return h, 0


def sidetone_report(level):
    # d0 40 00*19 N  (mascara 0x40 = sidetone), 22 bytes
    b = bytearray(22)
    b[0] = 0xD0
    b[1] = 0x40
    b[21] = level & 0x0F
    return bytes(b)


def eq_report(preset):
    # d0 04 P 00*19  (mascara 0x04 = EQ)
    b = bytearray(22)
    b[0] = 0xD0
    b[1] = 0x04
    b[2] = preset & 0xFF
    return bytes(b)


def set_feature(h, data):
    buf = ctypes.create_string_buffer(data, len(data))
    ok = hid.HidD_SetFeature(h, buf, len(data))
    return bool(ok), (0 if ok else ctypes.get_last_error())


def main():
    path, caps = rs.find_col04()
    if not path:
        print("COL04 NAO encontrada. Dongle plugado? MI_03 habilitado?")
        sys.exit(1)
    print("COL04:", path)

    h, err = open_rw(path)
    if not h:
        print("Abrir RW FALHOU err=%d %s" % (err, ctypes.FormatError(err).strip()))
        if err == 32:
            print("  -> err 32 = a Sony (ou outro processo) ainda segura o handle.")
            print("     Feche 'PlayStation Link.exe' e rode de novo.")
        sys.exit(2)
    print("Abrir RW OK  <-- COL04 aceita GENERIC_READ|GENERIC_WRITE")

    data = None
    if "--sidetone" in sys.argv:
        n = int(sys.argv[sys.argv.index("--sidetone") + 1])
        data = sidetone_report(n)
        print("Enviando sidetone nivel %d: %s" % (n, data.hex(" ")))
    elif "--eq" in sys.argv:
        p = int(sys.argv[sys.argv.index("--eq") + 1])
        data = eq_report(p)
        print("Enviando EQ preset 0x%02x: %s" % (p, data.hex(" ")))

    if data is not None:
        ok, werr = set_feature(h, data)
        if ok:
            print("SetFeature OK")
        else:
            print("SetFeature FALHOU err=%d %s" % (werr, ctypes.FormatError(werr).strip()))
        time.sleep(0.2)
        # le 0xB0 de volta pra confirmar que o device segue vivo/respondendo
        rs.hid.HidD_GetFeature.argtypes = [ctypes.c_void_p, ctypes.c_void_p, wt.ULONG]
        raw, gerr = rs.get_report(h, 0xB0)
        if raw:
            d = rs.decode(raw)
            print("read-back 0xB0: vol=%d/13 mic=%s botoes=%s" %
                  (d["vol"], "MUTADO" if d["mic_muted"] else "ativo", "+".join(d["buttons"])))
        else:
            print("read-back 0xB0 FAIL err=%d" % gerr)

    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
