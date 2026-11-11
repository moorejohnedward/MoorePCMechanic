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
  - `release.yml`: when a version tag is pushed (for example `v0.2.0`), GitHub builds the .exe, attaches its fingerprint and publishes a Release with notes pulled from CHANGELOG.md.

**Concepts:** version control with Git, semantic versioning (major.minor.patch), continuous integration and delivery (GitHub Actions), REST APIs (GitHub Releases API, JSON parsing), async downloads with progress reporting, cryptographic hashing for integrity.

**Result:** New versions now reach the app with one click from inside it. No local build needed after this one.
