# -*- coding: utf-8 -*-
"""Prototipo WinUSB do app do Pulse Elite / dongle PS Link (VID 054C PID 0ECC).

E o EXPERIMENTO QUE FECHA a incognita do HANDOFF secao 8.5: com o MI_03 rebindado
pra WinUSB (via Zadig), fazer o keepalive/presenca APENAS pelo poll de controle no
EP0 (GET_REPORT Feature 0xB0 a ~5 Hz) -- SEM NUNCA tocar no pipe de interrupcao
0x81 (a fonte do babble/freeze). Se o headset permanecer conectado e auto-sincronizar
apenas com esse poll, a arquitetura esta provada ponta-a-ponta.

Pre-requisito: rebindar MI_03 pra WinUSB (Zadig -> Options -> List All Devices ->
selecionar "PlayStation Link" interface 3 (MI_03) -> Install/Replace com WinUSB).
Reversivel pelo Gerenciador de Dispositivos (Reverter/Desinstalar driver).

Transferencias (bytes ja validados na captura, HANDOFF secoes 3-5, 8.4):
  GET_REPORT Feature 0xB0 : bmRequestType 0xA1, bRequest 0x01, wValue 0x03B0, wIndex 3, len 64
  SET_REPORT Feature 0xD0 : bmRequestType 0x21, bRequest 0x09, wValue 0x03D0, wIndex 3, data 22B

Uso:
  py winusb_proto.py                 # poll continuo a 5 Hz (o teste de presenca). Ctrl-C p/ sair.
  py winusb_proto.py --secs 120      # roda por 120 s e sai
  py winusb_proto.py --sidetone N    # 1x: envia sidetone nivel N (0-15) e le de volta
  py winusb_proto.py --eq P          # 1x: envia EQ preset P (0/32/64) e le de volta
"""
import sys, time
import usb.core, usb.util
import libusb_package

VID, PID = 0x054C, 0x0ECC
IFACE = 3  # MI_03

# --- transfer constants ---
GET_REPORT = dict(bmRequestType=0xA1, bRequest=0x01)   # class, interface, IN
SET_REPORT = dict(bmRequestType=0x21, bRequest=0x09)   # class, interface, OUT
FEATURE = 0x03  # report type Feature -> wValue high byte


def wvalue(report_type, report_id):
    return (report_type << 8) | report_id


def find_dev():
    dev = libusb_package.find(idVendor=VID, idProduct=PID)
    return dev


def ensure_iface(dev):
    """Reivindica a interface 3 E prova que ela responde via WinUSB.

    O claim do libusb num composite retorna sucesso mesmo quando a interface
    ainda esta no HidUsb (falso positivo); o teste real e um GET_REPORT: se
    falhar com I/O Error, o MI_03 ainda NAO esta no WinUSB. Retorna (ok, msg)."""
    try:
        # NAO chamar set_configuration num composite ja configurado (pode resetar).
        usb.util.claim_interface(dev, IFACE)
    except (usb.core.USBError, NotImplementedError) as e:
        return False, "nao consegui reivindicar a interface %d: %s" % (IFACE, e)
    # porteiro de verdade: um GET_REPORT precisa completar
    try:
        get_report(dev, 0xB0)
        return True, "MI_03 respondendo via WinUSB (GET_REPORT 0xB0 ok)"
    except usb.core.USBError as e:
        return False, ("GET_REPORT falhou (%s) -> o MI_03 provavelmente ainda esta\n"
                       "   no HidUsb. Rebinde a interface 3 pra WinUSB com o Zadig e rode de novo." % e)


def get_report(dev, report_id, length=64):
    return dev.ctrl_transfer(GET_REPORT["bmRequestType"], GET_REPORT["bRequest"],
                             wvalue(FEATURE, report_id), IFACE, length, timeout=1000)


def set_report(dev, data):
    return dev.ctrl_transfer(SET_REPORT["bmRequestType"], SET_REPORT["bRequest"],
                             wvalue(FEATURE, data[0]), IFACE, bytes(data), timeout=1000)


def decode(raw):
    b39, b43, b44 = raw[39], raw[43], raw[44]
    btns = []
    if b39 & 0x08: btns.append("VOL+")
    if b39 & 0x10: btns.append("VOL-")
    if b39 & 0x20: btns.append("MUTE")
    return dict(vol=b44, mic_muted=(b43 == 0x0C), buttons=btns or ["-"],
                b39=b39, b43=b43, b44=b44)


def sidetone_report(level):
    b = bytearray(22); b[0] = 0xD0; b[1] = 0x40; b[21] = level & 0x0F
    return b


def eq_report(preset):
    b = bytearray(22); b[0] = 0xD0; b[1] = 0x04; b[2] = preset & 0xFF
    return b


def fmt(d):
    return "vol=%2d/13  mic=%s  botoes=%s" % (
        d["vol"], "MUTADO" if d["mic_muted"] else "ativo", "+".join(d["buttons"]))


def main():
    dev = find_dev()
    if dev is None:
        print("Device 054C:0ECC nao encontrado pelo libusb. Dongle plugado?")
        sys.exit(1)
    print("Device encontrado:", dev)
    ok, msg = ensure_iface(dev)
    print(("OK: " if ok else "FALHA: ") + msg)
    if not ok:
        sys.exit(2)

    # ---- modos de escrita 1x ----
    if "--sidetone" in sys.argv or "--eq" in sys.argv:
        if "--sidetone" in sys.argv:
            n = int(sys.argv[sys.argv.index("--sidetone") + 1])
            data = sidetone_report(n); print("SET sidetone nivel %d: %s" % (n, bytes(data).hex(" ")))
        else:
            p = int(sys.argv[sys.argv.index("--eq") + 1])
            data = eq_report(p); print("SET EQ preset 0x%02x: %s" % (p, bytes(data).hex(" ")))
        try:
            n = set_report(dev, data); print("SET_REPORT OK (%d bytes)" % n)
        except usb.core.USBError as e:
            print("SET_REPORT FALHOU:", e)
        time.sleep(0.2)
        try:
            raw = get_report(dev, 0xB0); print("read-back 0xB0:", fmt(decode(raw)))
        except usb.core.USBError as e:
            print("read-back FALHOU:", e)
        usb.util.dispose_resources(dev)
        return

    # ---- poll continuo (TESTE DE PRESENCA) ----
    secs = None
    if "--secs" in sys.argv:
        secs = float(sys.argv[sys.argv.index("--secs") + 1])
    print("\n=== POLL DE PRESENCA @5Hz no EP0 (pipe interrupt 0x81 NUNCA aberto) ===")
    print("Ligue/desligue o headset e observe se ele auto-sincroniza e PERMANECE conectado.")
    print("Heartbeat a cada 5s + linha imediata em qualquer mudanca. Ctrl-C p/ sair.\n")
    t0 = time.time()
    last_state = None
    last_beat = 0.0
    fails = 0
    try:
        while True:
            now = time.time() - t0
            if secs is not None and now >= secs:
                break
            try:
                raw = get_report(dev, 0xB0)
                fails = 0
                d = decode(raw)
                state = fmt(d)
                if state != last_state:
                    print("[%6.1fs] MUDANCA: %s | raw[39,43,44]=%02x %02x %02x" %
                          (now, state, d["b39"], d["b43"], d["b44"]))
                    last_state = state
                elif now - last_beat >= 5.0:
                    print("[%6.1fs] ok       %s" % (now, state))
                    last_beat = now
            except usb.core.USBError as e:
                fails += 1
                print("[%6.1fs] POLL FALHOU (#%d): %s" % (now, fails, e))
                if fails >= 25:  # ~5s sem resposta
                    print("  -> device parou de responder (desconectou/removeu?). Encerrando.")
                    break
            time.sleep(0.2)
    except KeyboardInterrupt:
        print("\ninterrompido pelo usuario.")
    finally:
        usb.util.dispose_resources(dev)
        print("total: %.1fs" % (time.time() - t0))


if __name__ == "__main__":
    main()
