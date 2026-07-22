# Pulse Elite Companion (`pslink-libusb`)

A tiny Windows companion for the **Sony PlayStation Pulse Elite** wireless headset (via the
**PS Link** USB dongle) that does two things the official software can't:

1. **Fixes the USB-audio freeze** that plagues the PS Link dongle on Windows — audio stutters
   then dies, requiring a service restart — **without** any kernel driver or watchdog.
2. **Restores the headset controls** (sidetone, EQ, live volume/mic status) from a lightweight
   system-tray app, so you don't need the official "PlayStation Link" desktop app running.

> Not affiliated with, endorsed by, or supported by Sony. Use at your own risk.
> "Pulse Elite Companion" is the friendly name; `pslink-libusb` is the technical/repo name.

---

## The problem

On Windows, the PS Link dongle (`VID 054C / PID 0ECC`) periodically **freezes the audio graph**:
`audiodg` locks up, sound cuts out, and only restarting the audio service recovers it. It happens
passively during playback and is **accelerated** by pressing the headset's volume/mute buttons —
in the worst case it dies within **seconds** of button mashing during exclusive-mode playback.

## What actually causes it

Live USB bus captures (USBPcap/Wireshark) pinned the cause:

- Every time the headset reports a state change, the dongle pushes a **256-byte transfer into a
  64-byte interrupt endpoint (`0x81`)** → `USBD_STATUS_BABBLE_DETECTED`. Windows' error recovery
  (abort/reset pipe) tears down the **isochronous audio stream on the same device**, and `audiodg`
  freezes.
- That interrupt endpoint is only ever polled because the in-box **HID driver (`HidUsb`)** keeps
  it open. Nothing about the audio path itself is broken.
- Meanwhile, the headset's **"a PC is here" presence** is kept alive by a completely separate
  mechanism: a **~5 Hz `GET_REPORT(Feature 0xB0)` control poll on endpoint 0** — not the interrupt
  pipe. (Verified: the headset stayed connected for 130 s with the interrupt pipe totally silent,
  as long as the EP0 poll kept running.)

## The fix

Rebind the dongle's HID interface (**MI_03**) from `HidUsb` to **WinUSB**, then talk to it from
user space:

- WinUSB **never opens the interrupt pipe `0x81` on its own** → the babble transfer never gets a
  pipe to land in → **no freeze**. (Confirmed: a scenario that froze in <10 s ran clean.)
- The app runs the **same ~5 Hz `GET_REPORT(0xB0)` control poll** → **presence is preserved**, the
  headset auto-syncs and stays connected. (This is mandatory and continuous — it's the keepalive,
  not just button latency.)
- Buttons keep working because volume/mute are applied **device-side** in the headset's own DSP.
- Sidetone and EQ are set via `SET_REPORT(0xD0)` control transfers (validated byte-for-byte from
  captures of the official app).

The in-box USB **audio** driver (`usbaudio` on MI_00) is left completely untouched — sound keeps
working normally.

## Status

| Piece | State |
|---|---|
| Freeze eliminated (WinUSB, interrupt pipe never opened) | ✅ proven live |
| Presence / keepalive via EP0 poll | ✅ proven live (150 s, auto-sync) |
| Read volume / mic / buttons | ✅ |
| Sidetone + EQ control | ✅ |
| **Volume control from the app** (host→device, device-side DSP) | ✅ `SET_REPORT(0xD0)` mask `0x02` |
| System-tray app (C#/.NET) | 🚧 in progress |
| Dedicated settings window (sliders, battery) | 🚧 in progress |
| Mic mute **from the app** (host→device) | 🔬 under investigation (fallback: Windows software mute) |
| Battery indicator | 🔬 under investigation (report `0x82`) |
| Persistent WinUSB bind via own INF (drop Zadig) | 🚧 planned |

## Repository layout

- `app/PulseTray/` — the C#/.NET tray application (the deliverable).
- `winusb_proto.py` — the WinUSB prototype: 5 Hz keepalive poll + sidetone/EQ, never opens the
  interrupt pipe. `read_state.py`, `test_write.py`, `probe_volume_mic.py`, `probe_volume_map.py`,
  `app/pulse_device.py` — prototype/repro tools (pyusb + libusb) that validate the mechanism.

## Try it (prototype)

Requires Windows, a PS Link dongle, and [Zadig](https://zadig.akeo.ie).

1. **Rebind MI_03 to WinUSB** with Zadig: *Options → List All Devices*, pick
   **"PlayStation Link Adapter (Interface 3)"** (current driver **HidUsb** — **not** the
   `USBAudio` one, which is the audio interface), target **WinUSB**, *Replace Driver*.
   Reversible in Device Manager (uninstall device → scan for hardware changes).
2. Build the app (needs the .NET SDK):
   ```
   dotnet build app/PulseTray/PulseTray.csproj -c Debug
   ```
   Or verify the mechanism first with the Python tools:
   ```
   pip install pyusb libusb-package
   py read_state.py --watch      # live button/volume readout
   py winusb_proto.py            # 5 Hz keepalive + sidetone/EQ
   ```

## License

MIT — see [LICENSE](LICENSE).
