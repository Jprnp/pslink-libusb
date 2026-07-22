# -*- coding: utf-8 -*-
"""Sondagem do comando host->device de VOLUME e MUTE do mic (Pulse Elite / PS Link).

Contexto (HANDOFF §8.4/§9): a captura NUNCA mostrou comando de volume/mute vindo do
host -- os botoes alteram device-side (byte 44 / byte 43) e a Sony espelhava no Windows
via Core Audio. Este script SONDA se existe um comando host->device pra setar esses
estados, usando o READ-BACK como feedback: escreve um candidato, le o 0xB0, e ve se o
byte 44 (volume) / 43 (mute) mudou pro alvo. Para no primeiro acerto e RESTAURA o estado.

⚠️ RODAR COM AUDIO PARADO. Pre-requisito: MI_03 no WinUSB, app C# de teste FECHADO
(so um processo pode ter o handle WinUSB). Requer pyusb + libusb-package.

Uso:
  py probe_volume_mic.py --volume    # sonda comando de volume
  py probe_volume_mic.py --mic       # sonda comando de mute do mic
  py probe_volume_mic.py --volume --mic
"""
import sys, time
import usb.core, usb.util
import libusb_package

VID, PID, IFACE = 0x054C, 0x0ECC, 3
_GET = (0xA1, 0x01)
_SET = (0x21, 0x09)
_FEATURE = 0x03
OFF_MIC, OFF_VOL = 43, 44

log_lines = []
def log(s):
    print(s); log_lines.append(s)

def wval(rid): return (_FEATURE << 8) | rid

def get(dev, rid=0xB0, n=64):
    return bytes(dev.ctrl_transfer(_GET[0], _GET[1], wval(rid), IFACE, n, timeout=1000))

def setr(dev, data):
    return dev.ctrl_transfer(_SET[0], _SET[1], wval(data[0]), IFACE, bytes(data), timeout=1000)

def open_dev():
    dev = libusb_package.find(idVendor=VID, idProduct=PID)
    if dev is None:
        log("device nao encontrado"); sys.exit(1)
    try: usb.util.claim_interface(dev, IFACE)
    except Exception: pass
    try:
        get(dev)
    except usb.core.USBError as e:
        log("MI_03 nao responde via WinUSB (%s). App C# fechado? MI_03 no WinUSB?" % e); sys.exit(2)
    return dev

def probe_generic(dev, name, offset, target_fn, candidates):
    """Tenta cada candidato; le byte[offset] de volta; sucesso se == alvo. Restaura no fim."""
    base = get(dev)
    orig = base[offset]
    target = target_fn(orig)
    log("\n=== SONDA %s === estado atual byte[%d]=0x%02x, alvo=0x%02x" % (name, offset, orig, target))
    hit = None
    for desc, data in candidates(base, target):
        try:
            setr(dev, data)
        except usb.core.USBError as e:
            log("  [x] %-40s SET falhou: %s" % (desc, e)); continue
        time.sleep(0.15)
        try:
            now = get(dev)[offset]
        except usb.core.USBError as e:
            log("  [!] %-40s device parou de responder apos escrita: %s -- ABORTANDO" % (desc, e)); break
        mark = "<<< ACERTOU" if now == target else ("(mudou p/ 0x%02x)" % now if now != orig else "")
        log("  [%s] %-40s -> byte[%d]=0x%02x %s" % ("OK" if now == target else "  ", desc, offset, now, mark))
        if now == target:
            hit = desc; break
        if now != orig:
            log("      ~ efeito colateral: mudou mas nao pro alvo; anotar '%s'" % desc)
    # restaura
    try:
        # tenta reescrever o 0xB0 original; se nao for gravavel, os botoes fisicos corrigem
        setr(dev, bytearray(base)); time.sleep(0.1)
    except usb.core.USBError:
        pass
    if hit:
        log("  >>> %s: comando encontrado -> %s" % (name, hit))
    else:
        log("  >>> %s: nenhum candidato funcionou (provavel fallback Core Audio)" % name)
    return hit

def volume_candidates(base, target):
    # (A) reescrever o proprio 0xB0 com byte44 = alvo
    b = bytearray(base); b[OFF_VOL] = target
    yield ("0xB0 writeback (byte44=alvo)", b)
    # (B) varredura de mascara no 0xD0, valor em varias posicoes
    for mask in (0x01, 0x02, 0x08, 0x10, 0x20, 0x80):
        for pos in (2, 3, 4, 21):
            d = bytearray(22); d[0] = 0xD0; d[1] = mask; d[pos] = target
            yield ("0xD0 mask=0x%02x val@%d" % (mask, pos), d)

def mic_candidates(base, target):
    b = bytearray(base); b[OFF_MIC] = target
    yield ("0xB0 writeback (byte43=alvo)", b)
    for mask in (0x01, 0x02, 0x08, 0x10, 0x20, 0x80):
        for pos in (2, 3, 4):
            d = bytearray(22); d[0] = 0xD0; d[1] = mask; d[pos] = target
            yield ("0xD0 mask=0x%02x val@%d" % (mask, pos), d)

def main():
    if not any(a in sys.argv for a in ("--volume", "--mic")):
        print(__doc__); sys.exit(0)
    dev = open_dev()
    log("device OK via WinUSB. Baseline: " + get(dev)[:46].hex(" "))
    if "--volume" in sys.argv:
        # alvo: bem diferente do atual, dentro de 0..15
        probe_generic(dev, "VOLUME", OFF_VOL,
                      lambda cur: 3 if cur >= 8 else 12, volume_candidates)
    if "--mic" in sys.argv:
        # alvo: alterna 0xFC(ativo)<->0x0C(mutado)
        probe_generic(dev, "MIC-MUTE", OFF_MIC,
                      lambda cur: 0x0C if cur != 0x0C else 0xFC, mic_candidates)
    usb.util.dispose_resources(dev)
    open("probe_result.log", "w", encoding="utf-8").write("\n".join(log_lines))
    log("\nlog salvo em probe_result.log")

if __name__ == "__main__":
    main()
