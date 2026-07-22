# -*- coding: utf-8 -*-
"""Camada de device do app do Pulse Elite / dongle PS Link (VID 054C PID 0ECC).

Encapsula tudo que o prototipo `winusb_proto.py` provou ao vivo (ver HANDOFF §0):
  - keepalive/presenca: poll GET_REPORT(Feature 0xB0) a ~5 Hz SO no EP0
    (control transfer). O pipe interrupt 0x81 NUNCA e aberto -> sem babble -> sem freeze.
  - leitura de estado: volume (byte 44, 0-15), mute do mic (byte 43), botoes (byte 39).
  - escrita: sidetone (SET_REPORT 0xD0 mascara 0x40) e EQ (mascara 0x04).
  - reconexao automatica em replug/remocao do device.

Pre-requisito: MI_03 rebindado pra WinUSB (Zadig no protótipo; INF proprio no app final).
Requer: pyusb + libusb-package.

Uso como lib:
    dev = PulseDevice(on_change=lambda s: print(s))
    dev.start()               # sobe a thread de keepalive (presenca + leitura)
    dev.set_sidetone(8)
    dev.set_eq(PulseDevice.EQ_PRESETS[1])
    ...
    dev.stop()
"""
import threading
import time

import usb.core
import usb.util
import libusb_package

VID, PID = 0x054C, 0x0ECC
IFACE = 3  # MI_03

# --- HID-over-control constantes (bytes validados na captura, HANDOFF §3-5, §8.4) ---
_GET_BMREQ, _GET_REQ = 0xA1, 0x01   # class/interface, IN  -> GET_REPORT
_SET_BMREQ, _SET_REQ = 0x21, 0x09   # class/interface, OUT -> SET_REPORT
_FEATURE = 0x03                     # report type (byte alto do wValue)

# offsets decodificados do report 0xB0 (64 B, byte0 = report id)
_OFF_BTN, _OFF_MIC, _OFF_VOL = 39, 43, 44
VOL_MIN, VOL_MAX = 0, 15           # byte 44 (corrigido 2026-07-22; era 0-13)
SIDETONE_MIN, SIDETONE_MAX = 0, 15  # faixa da UI da Sony (descriptor e opaco)

POLL_INTERVAL = 0.2                 # 5 Hz -- NAO e latencia de botao, e o keepalive de presenca
_FAILS_BEFORE_RECONNECT = 10        # ~2 s sem resposta -> tenta reenumerar


def _wvalue(report_type, report_id):
    return (report_type << 8) | report_id


class PulseState:
    """Snapshot imutavel do estado lido do headset."""
    __slots__ = ("vol", "mic_muted", "btn_volup", "btn_voldn", "btn_mute", "connected", "raw")

    def __init__(self, raw=None):
        if raw is None:
            self.vol = None
            self.mic_muted = None
            self.btn_volup = self.btn_voldn = self.btn_mute = False
            self.connected = False
            self.raw = None
            return
        b = raw[_OFF_BTN]
        self.vol = raw[_OFF_VOL]
        self.mic_muted = (raw[_OFF_MIC] == 0x0C)
        self.btn_volup = bool(b & 0x08)
        self.btn_voldn = bool(b & 0x10)
        self.btn_mute = bool(b & 0x20)
        self.connected = True
        self.raw = bytes(raw)

    def key(self):
        """Tupla p/ detectar mudanca (ignora ruido de bytes estaticos)."""
        return (self.connected, self.vol, self.mic_muted,
                self.btn_volup, self.btn_voldn, self.btn_mute)

    def __repr__(self):
        if not self.connected:
            return "PulseState(desconectado)"
        btns = "".join(c for c, on in
                       (("+", self.btn_volup), ("-", self.btn_voldn), ("M", self.btn_mute)) if on) or "-"
        return "PulseState(vol=%s/15 mic=%s btn=%s)" % (
            self.vol, "mute" if self.mic_muted else "on", btns)


class PulseDevice:
    EQ_PRESETS = (0x00, 0x20, 0x40)  # 3 presets observados na captura

    def __init__(self, on_change=None, on_connection=None):
        """on_change(state): chamado quando o estado LOGICO muda (vol/mic/botao).
        on_connection(bool): chamado quando presenca do device sobe/cai."""
        self._on_change = on_change
        self._on_connection = on_connection
        self._dev = None
        self._lock = threading.Lock()       # serializa ctrl_transfer (poll vs escrita)
        self._thread = None
        self._stop = threading.Event()
        self._present = False
        self.state = PulseState()

    # ---------- ciclo de vida ----------
    def start(self):
        if self._thread and self._thread.is_alive():
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._run, name="pulse-keepalive", daemon=True)
        self._thread.start()

    def stop(self):
        self._stop.set()
        if self._thread:
            self._thread.join(timeout=2)
        self._dispose()

    # ---------- descoberta / conexao ----------
    def _open(self):
        dev = libusb_package.find(idVendor=VID, idProduct=PID)
        if dev is None:
            return False
        try:
            usb.util.claim_interface(dev, IFACE)
        except (usb.core.USBError, NotImplementedError):
            pass  # claim e falso-positivo em composite; o porteiro real e o GET_REPORT abaixo
        # porteiro: MI_03 tem que estar no WinUSB (senao GET_REPORT da I/O Error)
        try:
            dev.ctrl_transfer(_GET_BMREQ, _GET_REQ, _wvalue(_FEATURE, 0xB0), IFACE, 64, timeout=1000)
        except usb.core.USBError:
            usb.util.dispose_resources(dev)
            return False
        self._dev = dev
        return True

    def _dispose(self):
        if self._dev is not None:
            try:
                usb.util.dispose_resources(self._dev)
            except Exception:
                pass
            self._dev = None

    def _set_present(self, present):
        if present != self._present:
            self._present = present
            if self._on_connection:
                try:
                    self._on_connection(present)
                except Exception:
                    pass

    # ---------- loop de keepalive ----------
    def _run(self):
        fails = 0
        while not self._stop.is_set():
            if self._dev is None:
                if not self._open():
                    self._set_present(False)
                    self._stop.wait(0.5)   # device fora do ar; tenta de novo devagar
                    continue
                self._set_present(True)
                fails = 0
            try:
                with self._lock:
                    raw = self._dev.ctrl_transfer(_GET_BMREQ, _GET_REQ,
                                                  _wvalue(_FEATURE, 0xB0), IFACE, 64, timeout=1000)
                fails = 0
                self._update_state(PulseState(raw))
            except usb.core.USBError:
                fails += 1
                if fails >= _FAILS_BEFORE_RECONNECT:
                    self._dispose()          # forca reenumeracao no proximo laco
                    self._set_present(False)
                    fails = 0
            self._stop.wait(POLL_INTERVAL)

    def _update_state(self, new):
        old = self.state
        self.state = new
        if new.key() != old.key() and self._on_change:
            try:
                self._on_change(new)
            except Exception:
                pass

    # ---------- escrita ----------
    def _set_report(self, data):
        if self._dev is None:
            raise RuntimeError("device nao conectado")
        with self._lock:
            return self._dev.ctrl_transfer(_SET_BMREQ, _SET_REQ,
                                           _wvalue(_FEATURE, data[0]), IFACE, bytes(data), timeout=1000)

    def set_sidetone(self, level):
        level = max(SIDETONE_MIN, min(SIDETONE_MAX, int(level)))
        b = bytearray(22); b[0] = 0xD0; b[1] = 0x40; b[21] = level
        self._set_report(b)
        return level

    def set_eq(self, preset):
        b = bytearray(22); b[0] = 0xD0; b[1] = 0x04; b[2] = preset & 0xFF
        self._set_report(b)
        return preset


if __name__ == "__main__":
    # smoke test: sobe o keepalive e imprime mudancas por 20 s
    def _chg(s): print("  mudou:", s)
    def _conn(up): print("  presenca:", "UP" if up else "DOWN")
    d = PulseDevice(on_change=_chg, on_connection=_conn)
    print("subindo keepalive... aperte botoes no headset")
    d.start()
    try:
        time.sleep(20)
    finally:
        d.stop()
        print("parado.")
