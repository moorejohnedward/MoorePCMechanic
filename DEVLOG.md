# Moore PC Mechanic: Development Log

**Developer:** John Moore
**Project:** Moore PC Mechanic, a Windows system-health, monitoring and benchmark app (C#, WPF, .NET 8, Visual Studio)
**Goal:** One tool that combines what Driver Booster and System Mechanic do: health checks, live resource monitoring, benchmarks and upgrade advice, so I can tune my own PC and judge upgrades with real numbers.

Each entry records what was built, why, and the concepts behind it. The Git commit history on GitHub is the detailed, timestamped record of every change.

---

## 2026-11-08: v0.1, Phase 1 (inventory + live monitoring)

**Built**
- Overview tab with live tiles for CPU, GPU, video memory, system memory and the hottest drive, color-coded when hot.
- Full hardware inventory: CPU, motherboard/BIOS, each RAM stick's slot and part number, drives with health and free space, USB controllers.
- "Mechanic's notes": plain-English findings such as single-channel RAM, RAM running below its rated speed, an old BIOS or GPU driver, low disk space, drive wear and long uptime.
- Live sensors tab (min/max, filter, CSV logging), a processes tab (CPU and memory per program), a copyable text report and a "Restart as admin" button.
- Build-Exe.bat to produce a single portable .exe.

**Concepts:** WPF desktop UI (XAML + C#), reading hardware through WMI and LibreHardwareMonitor, admin elevation through an app manifest, single-file .NET publishing.

---

## 2026-11-10: v0.2, In-app updates + GitHub

**Problem:** Every upgrade meant running a full build on my PC.

**Built**
- **In-app updater** (`Services/UpdateService.cs`, `UpdateWindow.xaml`): the app checks GitHub for a newer release, shows what changed, downloads the new .exe, verifies it, swaps itself and restarts. It also has a "Check for updates on startup" option and "Skip this version."
- **Safe self-replacement:** Windows won't let a running .exe be overwritten, but it does allow renaming one. The updater renames the running file to `.old`, moves the new one into place, relaunches, and deletes `.old` on the next start. If the swap fails, the original is put back.
- **Integrity check:** each release includes a SHA-256 fingerprint. The updater refuses to install a download that doesn't match it.
- **GitHub repository + automated builds (CI/CD):**
  - `build.yml` compiles the app on GitHub's Windows servers on every push, so errors are caught before release.
  - `release.yml`: when the version number in the project file goes up (for example to 0.2.0), GitHub builds the .exe, attaches its fingerprint and publishes a Release (tag v0.2.0) with notes pulled from CHANGELOG.md.

**Concepts:** version control with Git, semantic versioning (major.minor.patch), continuous integration and delivery (GitHub Actions), REST APIs (GitHub Releases API, JSON parsing), async downloads with progress reporting, cryptographic hashing for integrity.

**Result:** New versions now reach the app with one click from inside it. No local build needed after this one.

---

## 2026-11-10: v0.3, Audio & calls tab + new icon

**Problem:** During Zoom classes, Windows kept switching away from my wireless headset (Razer Barracuda) to other devices, and the sound cut out. I need reliable audio for school and VA video sessions for the next 20+ weeks, and I'm keeping the wireless headset.

**Built**
- **Audio Guard** (`Services/AudioService.cs`): listens for Windows' audio device notifications. When the default speaker or mic changes to anything but my chosen headset, it sets the headset back for all three Windows roles (console, multimedia, communications). It backs off for a minute if another program keeps fighting it, so it can't flip-flop forever.
- **Dropout tracker:** records when the headset disappears and reappears, and for how long. This separates two different problems: Windows switching devices (software) versus the wireless link dropping (radio signal or USB power).
- **Call-ready check** (`MainWindow.Audio.cs`): opens the mic for 5 seconds to measure real input level, checks defaults, mute, volume and the camera, and offers a one-click fix.
- **System fixes:** Windows call "ducking" (registry), USB selective suspend (powercfg), per-port USB power saving (WMI, admin).
- **New icon:** dial torque wrench, drawn with a Python script.

**Concepts:** Windows Core Audio (WASAPI) through the NAudio library, COM interop (calling Windows' IPolicyConfig interface from C#), event-driven programming and thread marshaling to the UI thread, the Windows registry, WMI, partial classes to keep a large window's code organized.

**Delivery:** first version delivered through the in-app updater built in v0.2.

**v0.3.1 (same day):** icon redesigned as a mashup: torque wrench over the original gear and heartbeat line. The drawing script is saved in `tools/make_icon.py`. This small release doubles as a live test of the in-app updater.

**Tested 2026-11-10, 8:13 PM:** first real in-app update on my PC. v0.2.0 found v0.3.1 on GitHub, downloaded and verified it, swapped itself and restarted. The leftover old file was cleaned up automatically. The updater works end to end.

---

## 2026-11-11: v0.4, Drivers tab + Benchmarks tab

**Goal:** the two features that make this a real "Driver Booster + System Mechanic" replacement. Drivers should update with one click, and benchmarks should prove whether an upgrade (RAM, the planned RTX 5070 Ti, a driver) actually helped.

**Built: drivers** (`Services/DriverService.cs`, `MainWindow.Drivers.cs`)
- **Inventory:** WMI `Win32_PnPSignedDriver` lists every driver (maker, version, date). `Win32_PnPEntity` with a non-zero `ConfigManagerErrorCode` finds problem devices, translated from Device Manager's error codes into plain English.
- **Windows Update drivers:** uses the Windows Update Agent COM API (`Microsoft.Update.Session`) through C#'s `dynamic` late binding. It searches `IsInstalled=0 and Type='Driver'`, then downloads and installs each chosen update and reports the result and whether a restart is needed.
- **Safety net:** before any install, `SystemRestore.CreateRestorePoint` (WMI) makes an undo point. It also detects when Windows skips one because a restore point was already made in the last 24 hours.
- **NVIDIA one-click:** NVIDIA's public lookup service maps the card name to its product IDs and returns the newest driver version and download link. Windows shows NVIDIA versions differently (32.0.15.6614 = 566.14), so the app converts between the two. The installer is downloaded only from nvidia.com and must pass Windows' Authenticode check (`WinVerifyTrust`) with "NVIDIA Corporation" as the signer before it runs silently (`-s -noreboot`).

**Built: benchmarks** (`Services/BenchmarkService.cs`, `Services/GpuBenchmark.cs`, `MainWindow.Benchmarks.cs`)
- **Processor:** a mixed workload (integer hashing, branches, floating-point Mandelbrot) kept inside the CPU cache, timed on one thread and then on every thread. While it runs, the app samples temperature and core clocks from the sensor loop to spot throttling.
- **Memory:** 512 MB buffers (far bigger than any cache) read with SIMD vectors and copied on all cores for bandwidth. Latency comes from a 256 MB random pointer chase (Sattolo's shuffle), where every step depends on the previous one.
- **Drive:** a 2 GB file opened with Windows' file cache turned off (`FILE_FLAG_NO_BUFFERING` + write-through), so the drive itself is measured. It does 1 MB sequential writes and reads with 8 workers, plus 4 KB random reads at 1 and 16 at a time. The data is random so drives can't cheat by compressing it.
- **Graphics card:** a Direct3D 11 compute shader (HLSL, compiled at run time) runs about 2 million GPU threads of multiply-add chains to measure TFLOPS. Copying a 512 MB buffer on the card measures video memory bandwidth. The test picks the adapter with the most video memory and skips Windows' software renderer.
- **History:** every run is saved to `Documents\Moore PC Mechanic\benchmarks.json` with a label and a snapshot of the hardware (CPU, RAM configuration, GPU and driver version). The app compares runs and colors the changes.

**Testing on a real Windows machine:** I added a smoke test (`tests/Smoke`, `.github/workflows/smoke.yml`) that runs the driver and benchmark code on GitHub's Windows servers. Its first run caught three things before release:
- The CPU score scale was 100× too big.
- The memory latency test was only 32 MB, small enough to fit in a server CPU's cache, so latency looked too good.
- The graphics test chose Windows' software renderer on a machine with no real GPU.

All three were fixed. The NVIDIA lookup returned the current driver (621.14) for both an RTX 4070 and an RTX 5070 Ti.

**Concepts:** COM automation and late binding, WMI, System Restore, digital signatures (Authenticode), REST/XML/JSON parsing, multithreading with `Parallel.For` and atomics, SIMD (`Vector<T>`), unmanaged aligned memory and `unsafe` pointers, unbuffered file I/O, GPU compute shaders (Direct3D 11, HLSL), measurement design (warm-ups, best-of-N, defeating caches and compression), automated testing in CI.

---

**v0.4.2 and v0.4.3 (same evening): two real-world bugs**
- *0 °C processor:* LibreHardwareMonitor reports 0 for CPU temperature sensors it can't read (the PawnIO driver wasn't installed). Impossible temperatures are now treated as "no reading", and a "Turn on CPU temperature" button installs PawnIO after checking its signature.
- *"Windows blocked the update" in admin mode:* the update folder still held `MoorePCMechanic.exe.old` from the previous update. A second copy of the app (started minimized with Windows for Audio Guard) was still running from that old file, so Windows wouldn't let it be deleted. Renaming a running .exe is allowed, but deleting one is not. Fix: if the old file is locked, the updater moves the current version to a uniquely named `.old-<timestamp>` file. Cleanup removes every `.old*` file it can, and a freshly updated copy closes older copies still running. Lesson: in-place self-update has to assume more than one copy of the app may be running.

---

**v0.4.4: temperature units.** Added °F and K alongside °C (`Services/Temp.cs`). Sensors always report Celsius, so values stay in °C internally (warning thresholds, saved benchmark data) and are converted only for display: °F = °C × 9/5 + 32 and K = °C + 273.15. All display code goes through one formatter, so a single setting changes every screen, and the choice is saved in the user's AppData. Concepts: separating data from presentation, unit conversion, persisted user preferences.

---

## 2026-11-13: v0.5, Tune-up

**Goal:** one button that makes the PC run as smoothly and efficiently as possible, safely and reversibly.

**Built** (`Services/TuneUpService.cs`, `MainWindow.TuneUp.cs`)
- **Scan:** each check reports its current state, why it matters, and whether it can be fixed automatically (orange) or needs the user to act (blue advice).
- **Fixes:**
  - Power plan through `powercfg`, restoring the hidden built-in High performance plan if Windows has removed it.
  - Game Mode and hardware-accelerated GPU scheduling through the registry.
  - Junk cleanup: only Windows' disposable temp and cache folders, skipping files in use and temp files newer than a day.
  - Recycle Bin through the Shell API, `SHEmptyRecycleBin`.
  - SSD re-TRIM through `defrag /L`.
- **Startup apps:** read from the Run registry keys (64- and 32-bit) and the Startup folders. They're turned on and off through Explorer's `StartupApproved` values, the same mechanism Task Manager uses: first byte 02 = on, 03 = off, followed by a timestamp. The app's entries are never deleted.
- **Safety:**
  - A System Restore point is made before every tune-up.
  - Each change records its previous value in `tuneup-undo.json`, so "Undo" can restore the exact earlier state, including deleting a registry value that didn't exist before.
  - Deleted junk can't be restored, and the app says so.
- **Deep repair:** runs DISM `/RestoreHealth`, then `sfc /scannow`, reads their output (SFC writes UTF-16) and summarizes it.

**Testing:** the smoke test on GitHub's Windows servers ran a full scan, applied every fix (it cleared 287 MB of temp files on the test machine) and then the undo, before release.

**Concepts:** Windows power management, the registry (including 32/64-bit views and binary values), Shell API P/Invoke, running and parsing system tools, designing reversible changes (undo logs, restore points), user consent for destructive actions.

---
