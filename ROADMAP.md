# Roadmap

## Done
- WinUSB mechanism validated end-to-end: **audio freeze eliminated** (interrupt pipe `0x81`
  never opened) and **presence kept** via the 5 Hz `GET_REPORT(0xB0)` control poll on EP0.
- C#/.NET system-tray app: continuous keepalive, live read (volume / mic / buttons),
  sidetone, EQ.
- **Device-side volume control from the app** (`SET_REPORT(0xD0)` mask `0x02`).
- Dedicated settings window with sliders.
- **Portable device discovery** — matches by VID/PID and reads the interface GUID at runtime,
  so it works on any machine and any dongle (Zadig- or INF-bound).
- **Automated WinUSB driver installer** (`app/driver/install.ps1`, self-signed, built-in tools,
  no Zadig) + full uninstaller.
- Custom app / tray icon (color = connected, grey = disconnected).
- Autostart toggle ("start with Windows").
- **i18n** — English by default, with runtime-switchable Portuguese and Spanish.
- **Mic mute from the app** (device-side, `SET_REPORT(0xD0)` mask `0x01`, byte[2] 1/0).
- **Battery indicator** — feature report `0x82`, byte[3] on a 0–15 scale → percentage.
- **Connection state in the UI** — real headset link from `0xB0` byte 39 bit0 (Connected / Disconnected).

## To do
- **Idle auto-off (evaluate feasibility)** — a configurable polling timeout: after N minutes with
  no audio being played (e.g. 5), stop the keepalive so the headset powers itself off for lack of
  heartbeat, saving its battery. Detecting "no audio in use" would require reading render-endpoint
  activity via Core Audio (`IAudioMeterInformation`, read-only) — to be weighed against keeping the
  app WinUSB-only.
- **Packaging** — single-file self-contained `dotnet publish`; optionally one installer that
  bundles driver + app + autostart.
- **Properly signed driver** — Microsoft attestation signing to remove the self-signed-certificate
  step from the installer.
