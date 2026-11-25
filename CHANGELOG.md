# Changelog - Moore PC Mechanic

## v0.7.2 - Nov 24, 2026 (Default browser)
- **Make Google Chrome the default browser** (Tune-up tab, under the automatic tune-up). Shows which browser opens links now. One click opens Windows' Default apps page straight to Google Chrome; click **Set default** there and the app confirms the change within a few seconds. (Windows 10 and 11 don't let any program switch the default browser by itself, so this is the one click Windows requires.) If Chrome isn't installed, the button opens Google's Chrome download page instead.

## v0.7.1 - Nov 23, 2026 (Tune-up fits each PC, and ARM support)
- **The Tune up button now does the whole job.** It scans, then goes straight to "Apply these changes?" with everything picked for this PC. One OK, a restore point first, and "Undo last tune-up" still puts it all back.
- **Knows what kind of PC it's on** (shown at the top of the results): desktop, desktop with a graphics card, laptop, ARM PC, how much RAM, how many threads. It picks the fixes to match:
  - **Classic desktops:** High performance power plan, as before.
  - **Laptops, ARM PCs and Modern Standby PCs:** Windows hides that plan there, so it sets the power mode to **Best performance** while plugged in instead. Battery settings are left alone.
  - **GPU scheduling** is only offered when there's a separate graphics card.
  - **Every PC:** memory compression turned back on if something turned it off (and the SysMain service it needs).
- **Light PCs (8 GB or less, or 4 threads or fewer, like the HP AIO)** also get:
  - **Lighter visual effects:** no animations, fades, shadows or see-through panels, but smooth fonts and picture thumbnails are kept.
  - **Edge:** tabs sleep after 5 minutes, efficiency mode on, no preloading at startup or running after it's closed. Set as recommendations, so you can still change them in Edge.
  - **Startup trim:** unticks every startup app except security, sync (Tailscale, Google Drive, OneDrive, Dropbox), hardware drivers and the Moore apps. The confirmation lists them by name.
- **ARM PCs (Snapdragon / Windows on ARM):**
  - Each release now includes a native **MoorePCMechanic-arm64.exe**. On an ARM PC, "Check for updates" installs it, including on a copy that's running the x64 build under emulation now.
  - The tune-up lists startup apps that run through x64 emulation and says which common apps have ARM64 versions.
  - The CPU and motherboard sensors are left off in the ARM64 build (they use Intel/AMD-only instructions), and the "Turn on CPU temperature" (PawnIO) tip is replaced with an explanation, since PawnIO only works on Intel and AMD.

## v0.7.0 - Nov 23, 2026 (System check: hardware + security diagnostic)
- **New System check tab and header button.** One click runs a full hardware and security check side by side, built for bringing an old PC back to life. It only reads; every fix is its own button.
- **Hardware:** every drive's health (Windows' verdict, SMART failure prediction, bad and unreadable sectors, power-on hours, temperature, SSD wear, and a tip when Windows runs from a spinning hard drive), drive errors Windows logged in the last 30 days, hardware errors (WHEA) and blue screens with plain-English stop codes, sudden power losses, memory (matched set? all of it usable?) and the last Windows Memory Diagnostic result, temperatures and fans at rest, power-supply rails (+12 V, +5 V, +3.3 V against the ATX ±5% limits) and the CMOS battery, whether the clock is right (a dead CMOS battery makes old PCs wake up with the wrong date), problem devices, the network link speed, and on Windows 10, whether the PC can take Windows 11 (processor, TPM 2.0, UEFI/Secure Boot, memory, drive size).
- **Security** (also added to the Security tab's sweep): whether this version of Windows still gets security updates, how long since the last Windows security update (and whether updates are switched off or paused), old SMB1 file sharing, the Guest account, remote-control apps and Remote Desktop, and programs that no longer get security fixes (Flash, Java 6/7, Silverlight, QuickTime, Office 2007-2021, old Adobe Reader) or need updating (WinRAR before 7.13, 7-Zip before 25.00).
- **Quick memory test:** borrows most of the free memory and checks it with 6 patterns, twice (1-3 minutes). **Full test at restart** starts Windows Memory Diagnostic for every byte.
- **Power & cooling test (3, 10 or 30 minutes):** runs the processor and graphics card flat out while watching temperatures, throttling, power-supply voltages and the CPU fan. If the PC shuts off or restarts during the test, the app says so the next time it opens.
- **New fixes:** set the clock from the internet, turn the Windows Update service back on, turn off SMB1 and the Guest account, open Windows Update / Installed apps / the Drivers tab, start Windows Memory Diagnostic, get Microsoft's PC Health Check.
- **AMD Radeon support.** The Drivers tab now shows AMD graphics (a Radeon card and/or Ryzen built-in graphics): the installed AMD Software: Adrenalin Edition version, the Windows driver version and date, and which AMD driver branch the card is on (current, older Polaris/Vega cards that get fewer updates, or legacy cards AMD no longer updates). **Open AMD Software** launches the Adrenalin app to check for updates; if it isn't installed, **AMD's driver installer** opens AMD's Auto-Detect and Install page. AMD has no public lookup like NVIDIA's, so AMD's own tools pick the right driver for the exact card.
- The System check also lists every graphics driver (AMD, NVIDIA, Intel) with its age, and flags Windows' basic display driver (no real graphics driver installed).
- **Copy / Save report** puts the check, the test results and the full hardware inventory in one text file (Documents\Moore PC Mechanic\Reports).
- Includes the v0.6.0 Security tab (it hadn't been released yet).

## v0.6.0 - Nov 22, 2026 (Security tab)
- **New Security tab with a one-click security sweep** (changes nothing): Memory integrity, the vulnerable driver blocklist, risky or very old kernel drivers that are running (the usual reason Memory integrity won't turn on), drivers Windows blocked lately, Microsoft Defender (real-time, tamper protection, definitions age, last full scan, unwanted-app blocking, network protection), Secure Boot, TPM, sign-in (LSA) protection, firewall, UAC, Smart App Control, and the HP AIO Windows Update block.
- **A fix button on each finding:** turn the blocklist back on, remove a risky driver service (restore point first), update Defender, start a full scan, turn on unwanted-app blocking and network protection, block Windows Update drivers on the HP AIO, or jump to the right Windows Security page.
- **Security pause** for old vendor tools Windows blocks (like HP's AMI BIOS recovery USB maker): makes a restore point, turns off Memory integrity and the vulnerable driver blocklist for 1, 2 or 4 hours, and puts both back exactly as they were when you click "Resume protections now". If you forget, a scheduled task resumes them when time runs out (it also checks at every sign-in). A red banner shows while paused. Refuses to start when Smart App Control is on, since then the pause can't help and turning SAC off is permanent.
- Pause/resume history is kept in Documents\Moore PC Mechanic\security-history.txt.

## v0.5.2 - Nov 22, 2026 (HP AIO BIOS lock)
- **Knows about the HP 24-dd0 All-in-One BIOS problem.** On that AIO (MonkD board, SSID 86F2/86F3) Windows Update pushed a BIOS for the wrong board and left it stuck on "The System BIOS update failed - Unable to open BIOS image file".
- On those PCs the app never suggests a BIOS update: the "BIOS is over 18 months old" tip is replaced by a warning at the top of Mechanic's notes with the safe options (keep the current BIOS, block Windows Update drivers, recovery stick with Windows + B, RAM/SSD upgrades instead).
- The Drivers tab hides BIOS and firmware updates from Windows Update there, and offers to turn on "Do not include drivers with Windows Updates".

## v0.5.1 - Nov 13, 2026 (Automatic tune-up)
- **Tune up automatically every week.** On the Tune-up tab, tick "Tune up automatically every…" and pick a day and time (24-hour clock, default Sunday 0300). Windows Task Scheduler then runs the safe fixes with no window: power plan, Game Mode, junk cleanup and SSD re-TRIM. If the PC was off or asleep at that time, it runs soon after it's back on.
- The automatic run never restarts the PC, empties the Recycle Bin, changes startup apps or touches GPU scheduling. Anything that needs you is listed for the next time you open the Tune-up tab.
- A restore point is made before each automatic run. Results are shown under the schedule ("Last automatic tune-up: …") and kept in Documents\Moore PC Mechanic\tuneup-history.txt.

## v0.5.0 - Nov 13, 2026 (Tune-up)
- **New "Tune up" button** (top of the window) and **Tune-up tab**. One scan shows what's slowing the PC down. **Tune & optimize** fixes the ticked items in one click.
  - **Power plan:** switches to High performance, so the processor stops dropping into slow power-saving states between bursts.
  - **Game Mode:** turned on, so background maintenance stays out of the way of full-screen apps.
  - **Hardware-accelerated GPU scheduling:** turned on for the RTX card (takes effect after a restart).
  - **Junk cleanup:** temp folders, crash dumps, old error reports and Windows Update's delivery cache. Your own files are never touched. Emptying the Recycle Bin is a separate item that's left unticked.
  - **SSD re-TRIM:** keeps the SSDs writing at full speed, and turns TRIM back on if it's off.
  - **Startup apps:** the same on/off switches as Task Manager's Startup tab. Untick what you don't need.
  - **Advice:** low free space on C:, SSD running hot (with what to do about it), and a restart that's overdue or that Windows is waiting for.
- **Restore point first**, and **Undo last tune-up** puts the power plan, Game Mode, GPU scheduling and startup apps back.
- **Deep repair (20-30 min):** runs Windows' own DISM and System File Checker to find and fix damaged system files, then explains the result in plain English.

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
