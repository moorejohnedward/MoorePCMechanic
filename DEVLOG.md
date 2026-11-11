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

