# Changelog - Moore PC Mechanic

## v0.4.4 - Nov 11, 2026 (Fahrenheit and Kelvin)
- **Temperatures in °C, °F and K.** A new picker at the top of the window chooses °C, °F, K, or all three (the default). Dashboard tiles show the main number big with the other units underneath. Live sensors and benchmark notes show all three, e.g. "65.0 °C · 149.0 °F · 338.2 K". Your choice is remembered.
- Warnings still use the same limits (for example a hot CPU at 90 °C = 194 °F), whatever unit is shown.

## v0.4.3 - Nov 11, 2026 (Updater fix)
- **Fixed: "Windows blocked the update in this folder" even in admin mode.** A second, older copy of the app was still running (the one that starts minimized with Windows for Audio Guard). It kept the previous version's file locked, so the updater couldn't clear it. The updater now works around a locked old file, and after updating it closes any older copies still running.

## v0.4.2 - Nov 11, 2026 (CPU temperature fix)
- **Fixed: the processor showed 0 °C.** That was a missing reading, not a real temperature. Missing temperatures now show as "—" everywhere (dashboard, Live sensors, benchmark heat check) instead of 0 °C.
- **New "Turn on CPU temperature" button** (top right, shown only when needed). Reading CPU temperatures requires PawnIO, a free signed driver used by hardware monitors. The button downloads the official installer, checks its digital signature, runs it and restarts the app.

## v0.4.1 - Nov 11, 2026 (Benchmarks on more than one PC)
- Each benchmark run now records which PC it ran on, with a new **PC** column in Saved runs. New runs are compared only with the last run from the same PC. Your Documents folder syncs through OneDrive, so runs from your home PC and this one can end up in the same list without getting mixed up.

## v0.4.0 - Nov 11, 2026 (Drivers & Benchmarks)
- **New Drivers tab.** One button checks for driver updates, then one more downloads and installs them.
  - **Windows Update drivers:** lists the driver updates Windows Update has for this PC. Tick the ones you want and click **Download & install**. A System Restore point is made first so it can be undone.
  - **NVIDIA graphics driver:** compares your driver with NVIDIA's newest for your exact card. One click downloads it, confirms it's genuinely signed by NVIDIA, makes a restore point and installs it silently, keeping your settings.
  - **Problem devices:** anything Windows reports a problem with (the yellow warnings in Device Manager) shows at the top in plain English.
  - **Installed drivers:** every driver with its maker, version, date and age. Search it, or hide Microsoft's built-in ones.
  - Installing needs admin. The app offers to restart as admin, and offers a restart afterwards when a driver needs one.
- **New Benchmarks tab** (replaces the "coming soon" page):
  - **Processor:** one core and all threads. The all-thread test also watches temperature and clock speed for overheating (throttling); run as admin to include it.
  - **Memory:** read speed, copy speed and latency.
  - **Drive:** big-file read and write and small random reads, on any drive you pick (uses a 2 GB test file that is deleted afterwards).
  - **Graphics card:** compute power (TFLOPS) and video memory speed.
  - Name each run (e.g. "Before RAM upgrade"). Runs are saved, and each new run is compared with the last one, with gains in green and losses in red. Pick any two saved runs to compare them.

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
