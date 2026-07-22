# -*- coding: utf-8 -*-
"""Mapeia a ESCALA do comando de volume host->device (report 0xD0, mascara 0x02).

A sondagem anterior (probe_volume_mic.py) achou que `d0 02 <V> ...` altera o byte 44
(volume device-side). Este script varre V no byte 2 e le o byte 44 de volta pra
descobrir a relacao V -> volume, e RESTAURA um nivel confortavel no fim.

⚠️ audio parado. MI_03 no WinUSB, app C# fechado. Requer pyusb + libusb-package.
"""
import time
import usb.core, usb.util
import libusb_package

VID, PID, IFACE = 0x054C, 0x0ECC, 3
_GET, _SET, _FEAT = (0xA1, 0x01), (0x21, 0x09), 0x03
OFF_VOL = 44

def wval(rid): return (_FEAT << 8) | rid
def get(dev): return bytes(dev.ctrl_transfer(_GET[0], _GET[1], wval(0xB0), IFACE, 64, timeout=1000))
def vol_cmd(dev, v, pos=2):
    d = bytearray(22); d[0] = 0xD0; d[1] = 0x02; d[pos] = v & 0xFF
    dev.ctrl_transfer(_SET[0], _SET[1], wval(0xD0), IFACE, bytes(d), timeout=1000)

dev = libusb_package.find(idVendor=VID, idProduct=PID)
try: usb.util.claim_interface(dev, IFACE)
except Exception: pass
get(dev)  # gate

print("byte44 inicial:", get(dev)[OFF_VOL])
print("\n=== varredura mascara 0x02, valor no byte 2 ===")
print(" V escrito | byte44 lido")
table = []
for V in [0, 1, 2, 3, 4, 5, 6, 8, 10, 12, 15, 16, 20, 24, 30, 32, 45, 63, 100, 255]:
    vol_cmd(dev, V, pos=2)
    time.sleep(0.2)
    read = get(dev)[OFF_VOL]
    table.append((V, read))
    print("   %3d     |   %2d" % (V, read))

# tenta inferir e restaurar pra ~8/15
print("\n=== varredura no byte 3 (checar se o valor mora la) ===")
for V in [0, 4, 8, 15]:
    d = bytearray(22); d[0] = 0xD0; d[1] = 0x02; d[3] = V
    dev.ctrl_transfer(_SET[0], _SET[1], wval(0xD0), IFACE, bytes(d), timeout=1000)
    time.sleep(0.2)
    print("   byte3=%3d -> byte44=%2d" % (V, get(dev)[OFF_VOL]))

# restaura: acha o V que deu readback mais proximo de 8
best = min(table, key=lambda t: abs(t[1] - 8)) if table else (8, 0)
print("\nrestaurando volume ~8 usando V=%d (deu %d na varredura)..." % best)
vol_cmd(dev, best[0], pos=2)
time.sleep(0.2)
print("byte44 final:", get(dev)[OFF_VOL])
usb.util.dispose_resources(dev)
