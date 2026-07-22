# -*- coding: utf-8 -*-
"""Dump completo dos reports de status (0xB0, 0x82, 0x80, 0x84) via WinUSB.
Rodar com headset LIGADO e depois DESLIGADO pra diffar e achar o byte de conexao.
Uso: py read_full.py <rotulo>   (ex.: py read_full.py ligado)
"""
import sys, time
import usb.core, usb.util
import libusb_package

VID, PID, IFACE = 0x054C, 0x0ECC, 3
def wval(rid): return (0x03 << 8) | rid
def get(dev, rid, n=64): return bytes(dev.ctrl_transfer(0xA1, 0x01, wval(rid), IFACE, n, timeout=1000))

label = sys.argv[1] if len(sys.argv) > 1 else "estado"
dev = libusb_package.find(idVendor=VID, idProduct=PID)
try: usb.util.claim_interface(dev, IFACE)
except Exception: pass

out = []
for rid in (0xB0, 0x82, 0x80, 0x84):
    try:
        # le 2x pra estabilidade
        r = get(dev, rid); time.sleep(0.1); r2 = get(dev, rid)
        tag = "" if r == r2 else " (INSTAVEL!)"
        out.append("%02x: %s%s" % (rid, r.hex(" "), tag))
    except usb.core.USBError as e:
        out.append("%02x: erro %s" % (rid, e))
usb.util.dispose_resources(dev)

text = "=== %s ===\n%s\n" % (label, "\n".join(out))
print(text)
open("raw/fulldump_%s.txt" % label, "w", encoding="utf-8").write(text)
