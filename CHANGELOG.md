# Changelog - Moore PC Mechanic

## v0.3.1 - Nov 10, 2026 (Icon)
- **New icon mashup:** dial torque wrench in front of the gear, with the heartbeat line underneath.

## v0.3.0 - Nov 10, 2026 (Audio & calls)
- **New "Audio & calls" tab** for Zoom classes and VA video sessions with a wireless headset.
- **Audio Guard:** pick your headset once; if Windows or another program switches the speaker or mic, the app switches it back within a second and logs what took over.
- **Dropout tracker:** logs every time the headset's wireless link drops and reconnects, with how long it was gone (also saved to Logs\audio-events.csv).
- **Call-ready check:** one button tests headset connection, defaults, mute/volume, whether the mic hears you, and the camera. "Fix everything" repairs what it can.
- **Fixes for cutting out:** stop Windows lowering sound during calls, turn off USB power saving, keep USB ports awake (admin). Each can be undone.
- Turn unused audio devices on/off from the list; option to start with Windows (minimized) so the guard is always running.
- **New icon:** a dial torque wrench.

## v0.2.0 - Nov 10, 2026 (In-app updates)
- **Updates from inside the app:** new "Check for updates" button, plus a banner at startup when a new version is out. It shows what's new, then downloads, verifies and installs with one click and restarts the app.
- "Skip this version", "Later", and an on/off switch for the startup check.
- Your current version is only replaced after the download passes its safety check (SHA-256 fingerprint); if anything fails, the old version stays.
- The project now lives on GitHub (github.com/moorejohnedward/moorepcmechanic). Every new version is built there automatically, so upgrades no longer need Build-Exe.bat.
- Version number now shows under the app name.

## v0.1.0 - Nov 8, 2026 (Phase 1)
- New app: hardware inventory (CPU, board/BIOS, RAM per slot with part numbers, GPU + VRAM + driver, drives + health, volumes, USB controllers, network).
- Mechanic's notes: single-channel RAM, RAM below rated speed, old BIOS/GPU driver, low disk space, drive wear, long uptime, admin/CPU-temp hints.
- Live dashboard tiles: CPU, GPU, video memory, system memory, hottest drive (color warnings when hot).
- Live sensors tab: all sensors with min/max, filter box, reset, CSV logging.
- Processes tab: CPU / memory per program, grouped.
- Copy / save text report, "Restart as admin" button, custom icon.
