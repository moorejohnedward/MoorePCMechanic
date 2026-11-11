# Moore PC Mechanic

A system health, monitoring and (soon) benchmarking app for Windows. Think Driver Booster + System Mechanic, without the junk.

## Get it running

**Easiest:** double-click `Build-Exe.bat`. It builds one self-contained `MoorePCMechanic.exe` into `Release (app to run and share)` and opens that folder. Needs the .NET SDK (Visual Studio already installed it).

**In Visual Studio:** open `MoorePCMechanic.sln` and press F5 (the first build downloads the packages automatically).
For the shareable exe: right-click the project > **Publish** > **FolderProfile** > **Publish**.

Run it, then click **Restart as admin** (top right) to unlock temperatures, fan speeds, voltages and drive health.

## Updates

After the first install, you never need to rebuild for an upgrade. Click **Check for updates** in the top bar (the app also checks quietly each time it starts and shows a banner when a new version is out). It downloads the new version from this project's GitHub Releases, checks its SHA-256 fingerprint, swaps itself and restarts.

**How a new version gets published:** add an entry at the top of `CHANGELOG.md` (`## v0.3.0 - date`), raise `<Version>` in `MoorePCMechanic/MoorePCMechanic.csproj` to match, commit and push to `main`. GitHub Actions sees the new version number, builds the .exe on Windows and posts the Release; the changelog entry becomes the "What's new" text in the app.

School/progress record: `DEVLOG.md` and the commit history.

## Audio & calls (v0.3)

Built for video calls on a wireless headset. **Audio Guard** keeps your chosen headset as the Windows speaker and mic and switches it back if anything changes it. The event log shows device switches and wireless dropouts with times. **Call-ready check** tests everything before a meeting. One-click fixes cover the Windows settings that make sound cut out. Tick **Start with Windows** to keep the guard running all the time.

## What's in Phase 1 (v0.1)

- **Overview** - live tiles for CPU, GPU, video memory, system memory and the hottest drive, plus a full hardware inventory: exact CPU, motherboard and BIOS, every RAM stick with slot, part number and speed, GPU with VRAM and driver version, drives with health and free space, USB controllers (shows add-in cards), network.
- **Mechanic's notes** - plain-English findings: single-channel RAM (and the part number to match), RAM below rated speed (XMP/EXPO), old BIOS or GPU driver, drives getting full or wearing out, long uptime.
- **Copy / Save report** - a full text report. Paste it to Claude for upgrade advice.
- **Live sensors** - every temperature, clock, load, fan, voltage and power sensor with min/max, filterable. **Start logging** writes them to a CSV once a second (opens in Excel or Google Sheets).
- **What's using it** - programs ranked by CPU or memory, grouped (all Chrome windows count as one line).

Reports and logs are saved in `Documents\Moore PC Mechanic`.

## Built with

C# / WPF on .NET 8, [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) for sensors, Windows WMI for the inventory.

If CPU temperature stays blank even in admin mode, install the free PawnIO driver from pawnio.eu and restart the app.

## Roadmap

- **Phase 2:** benchmarks (CPU, memory bandwidth/latency, disk, GPU) with saved history and before/after comparisons; startup-program review; crash history; throttling check.
- **Phase 3:** upgrade advisor (power-supply headroom, RAM matching, free M.2 slots) and a driver checker that links to official sources.
