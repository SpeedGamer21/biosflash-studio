<p align="center">
  <img src="docs/logo.png" width="96" alt="BiosFlash Studio logo">
</p>

<h1 align="center">BiosFlash Studio</h1>

<p align="center">
  <b>A modern CH341A BIOS programmer with firmware repair tools — for Windows, Debian/Ubuntu and Fedora.</b><br>
  Read, write and verify SPI flash and I²C EEPROM chips, analyze Intel/AMD firmware, repair dumps from stock images and unpack vendor BIOS updates.
</p>

<p align="center">
  <img alt="Windows" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4">
  <img alt="Debian / Ubuntu" src="https://img.shields.io/badge/Debian%20%7C%20Ubuntu-.deb-A81D33">
  <img alt="Fedora" src="https://img.shields.io/badge/Fedora%20%7C%20RHEL-.rpm-51A2DA">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4">
  <img alt="Avalonia UI" src="https://img.shields.io/badge/UI-Avalonia%2011-8B44AC">
</p>

<p align="center">
  <img src="docs/screenshots/overview.png" alt="BiosFlash Studio overview" width="900">
</p>

---

## Contents

- [Highlights](#highlights)
- [Screenshots](#screenshots)
- [Download & install](#download--install)
- [Connecting a CH341A](#connecting-a-ch341a)
- [Features](#features)
- [Building from source](#building-from-source)
- [Testing](#testing)
- [Project layout](#project-layout)
- [Safety](#safety)
- [License](#license)

## Highlights

- **One app on three platforms.** Native installers for Windows, Debian/Ubuntu and Fedora/RHEL, each with the .NET runtime bundled.
- **CH341A done properly.** It works through the WCH vendor driver or WinUSB (Zadig) on Windows and through libusb on Linux, and picks the backend automatically. Chips are auto-detected by JEDEC ID, with an SFDP size cross-check and 4-byte addressing for 32 MB+ chips. Smart write only rewrites the 4 KB sectors that changed.
- **Firmware forensics.** It shows the Intel descriptor regions, ME/CSE version, DMI serial and UUID, the Windows OEM key, the GbE MAC, NVRAM variables and the AMD PSP.
- **Repair tools.** Clear NVRAM, clean the ME region, set the MAC, or rebuild a broken BIOS/ME/descriptor from stock firmware. The board's own identity (MAC, serial, OEM key) is carried over from its dump.
- **Update extractor and ASUS CAP tool.** Pull a flashable image out of InsydeFlash `.exe` files, capsules and ZIP downloads.
- **Boot logo editor.** Find the BIOS boot logo, even inside LZMA-compressed volumes, and replace it with your own picture. Safety nets make a wrong format, size or colour depth impossible.
- **Safe by default.** Every read is backed up automatically. There are optional double reads, verify-after-write, confirmations before destructive steps and undo for buffer edits.
- **No hardware needed to try it.** A built-in simulator and a synthetic demo firmware let you explore every feature.

## Screenshots

| | |
|:---:|:---:|
| ![Overview](docs/screenshots/overview.png) **Overview** — chip, programmer, firmware identity, DMI, OEM key | ![Light theme](docs/screenshots/light.png) **Light theme** |
| ![Firmware analysis](docs/screenshots/analysis.png) **Firmware Analysis** — descriptor regions, ME report, usage map, NVRAM | ![Hex editor](docs/screenshots/hex.png) **Hex Editor** — handles 64 MB+ images |
| ![Repair tools](docs/screenshots/repair.png) **Repair Tools** — NVRAM, ME, MAC, region merge, identity transfer | ![Update extractor](docs/screenshots/extractor.png) **Update Extractor** — images inside vendor updates |
| ![ASUS CAP tool](docs/screenshots/cap-tool.png) **ASUS CAP Tool** — strip the capsule, keep your MAC | ![Boot logo](docs/screenshots/boot-logo.png) **Boot Logo** — replace the logo with automatic conversion and checks |
| ![Debian](docs/screenshots/linux-debian.png) **Running on Debian 13** | ![Linux diagnostics](docs/screenshots/linux-diagnostics.png) **Linux diagnostics** — libusb, udev rule, permissions |

## Download & install

Get the file for your system from the **[Releases](../../releases)** page.

| System | File | Install |
|---|---|---|
| **Windows 10/11 (x64)** | `BiosFlashStudio-2.0.0-win-x64-setup.exe` | Run the installer. You can install for the current user (no admin) or for all users. It adds a Start-menu entry and a *demo mode* shortcut |
| **Debian / Ubuntu / Mint (x64)** | `biosflash-studio_2.0.0_amd64.deb` | `sudo apt install ./biosflash-studio_2.0.0_amd64.deb` |
| **Debian / Raspberry Pi OS (ARM64)** | `biosflash-studio_2.0.0_arm64.deb` | `sudo apt install ./biosflash-studio_2.0.0_arm64.deb` |
| **Fedora / RHEL / Rocky (x64)** | `biosflash-studio-2.0.0-1.x86_64.rpm` | `sudo dnf install ./biosflash-studio-2.0.0-1.x86_64.rpm` |
| **Fedora (ARM64)** | `biosflash-studio-2.0.0-1.aarch64.rpm` | `sudo dnf install ./biosflash-studio-2.0.0-1.aarch64.rpm` |
| **Any Linux (portable)** | `biosflash-studio-2.0.0-linux-x64.tar.gz` (or `-arm64`) | Unpack, then run `sudo sh install.sh`, or just `./BiosFlashStudio` |

The Linux packages pull in `libusb` automatically. They also install a udev rule so the programmer works **without root**: re-plug the CH341A once after installing. More details are in [packaging/linux/README-LINUX.md](packaging/linux/README-LINUX.md).

To look around without a programmer, start **BiosFlash Studio (demo mode)** from the Start menu, or run `biosflash-studio --demo`.

## Connecting a CH341A

1. **Set the jumper to SPI/I²C** (pins 1-2). In UART mode the board shows up as `1a86:5523` and the app tells you to move the jumper.
2. **Driver:**
   - **Windows:** install the official **WCH CH341PAR** driver (the same one AsProgrammer uses), *or* bind **WinUSB** to `1A86:5512` with [Zadig](https://zadig.akeo.ie/).
   - **Linux:** no driver is needed. libusb and the udev rule come with the package; *Diagnostics → Install udev rule* also works.
3. **Clip on:** pin 1 goes to the red wire. For laptops, unplug AC, remove the battery and the CMOS cell.
4. **Detect, then Read:** press **F5**, then **F6**. Every read is saved automatically to `Documents/BiosFlash Studio/Backups`.

## Features

### Programming
| | |
|---|---|
| **Backends** | WCH CH341DLL and WinUSB on Windows, libusb-1.0 on Linux, plus a simulator. The backend is chosen automatically, and a USB/driver/udev diagnostics page helps when it isn't |
| **Chips** | 160+ SPI NOR profiles (Winbond, Macronix, GigaDevice, EON, Micron, ISSI, XMC, SST, Spansion, Fudan, Zbit, Boya, Puya, XTX…), and 24C01–24C1024 I²C EEPROMs |
| **Detection** | JEDEC ID, SFDP density check, a generic fallback for unknown IDs, and 1.8 V chip warnings |
| **Operations** | Detect, Read, Erase (chip / 64 KB block / 4 KB sector), Blank check, Program, Verify. **Auto Program** runs unprotect → erase → blank → program → verify in one step |
| **Safety** | Smart write (only changed sectors), double-read contact check, verify after write, cancel at any time |
| **Chip functions** | Status-register viewer and editor, protection removal, RES/REMS/Unique-ID/SFDP dump, and range or single-region read/erase/write/verify |

### Analysis
- **Intel:** flash descriptor v1/v2 regions and access rights, ME/CSE/TXE version, release type, SVN, VCN, FITC, `$FPT` partitions, and a chipset estimate.
- **Identity:** SMBIOS DMI (brand, model, serial, UUID, SKU, board), the **Windows OEM key** (ACPI MSDM table) and the GbE MAC.
- **Firmware details:** BIOS vendor, version and date; UEFI NVRAM (VSS, authenticated VSS, AMI NVAR); AMD PSP / AGESA detection; a per-block flash usage map.
- **Hex editor:** search by hex, text or UTF-16; compare with a file; modified bytes are highlighted. Analysis results can be exported as a TXT report.

### Repair tools
- **Clear NVRAM:** resets BIOS settings; the store headers are kept intact.
- **Clean ME:** wipes the ME's configuration partitions (MFS/FLOG) for ME ≤ 10 and TXE. CSME 11+ is refused, because it has to be replaced from stock firmware.
- **Set MAC address:** writes the MAC into every Intel GbE NVM bank and recomputes the checksum.
- **Repair from stock:** load a stock image or a vendor update, then merge it with your dump region by region. One-click presets: *Repair BIOS region*, *Repair descriptor*, *Replace ME*, *Full stock, keep board data*.
- **Identity transfer:** copies the MAC, the NVRAM store, the SMBIOS serial/UUID tables and the MSDM key from the board's own dump into the repaired image, wherever the stock layout allows.
- **Binary tools:** extract a range, split the image (system BIOS / EC firmware), pad or truncate to a size, append files. Every buffer change can be **undone**.

### Boot logo editor
- **Finds every picture** (BMP, JPEG, PNG, GIF) in the firmware. That includes pictures inside **LZMA-compressed** UEFI volumes, the usual place for AMI Aptio logos. The list shows thumbnails, resolution, colour depth and the full path (file › LZMA › logo file).
- **Export** any picture, or **replace** it with your own (PNG, JPEG, BMP, GIF or WebP, any size).

**Safety nets.** Each one stops a replacement that could keep the board from booting:

| | |
|---|---|
| **Automatic conversion** | Your picture is converted to the *original's* format, resolution and colour depth (BMP 1/4/8/24/32-bit, baseline JPEG, PNG, single-frame GIF). You cannot pick a wrong one |
| **Fit** | The picture is fitted (or cropped, or stretched) to the exact logo resolution, with a background colour taken from the original |
| **Size** | The result must fit in the original's space. JPEG quality is lowered automatically; a size meter shows how much space is used |
| **Re-decode** | The converted file is decoded again and its header is validated before it can be used |
| **Rebuild** | Compressed volumes are recompressed as exact-length LZMA streams (checked against the xz and 7-Zip decoders). The freed space becomes a filler section, so no other file moves. FFS and CRC32 checksums are recomputed |
| **Verification** | The rebuilt firmware is parsed again. Checks: every checksum, every LZMA volume, the new logo byte-for-byte, and *nothing outside the logo's own file changed*. If any check fails, the buffer is not touched |
| **Protection** | Firmware with **Intel Boot Guard** is blocked unless you explicitly override. Pictures inside code modules or behind unsupported compression (Tiano, Brotli) are export-only |
| **Undo** | Only the buffer changes, so Undo works, and your original dump stays in the backups |

> Compressed logos are rebuilt in place. If your picture compresses worse than the original, it is refused with a clear message instead of moving other firmware files.

### Update extractor & ASUS CAP tool
- **Update extractor:** finds flashable images inside vendor updates:
  - Intel full-flash images
  - InsydeFlash `$_IFLASH` BIOS / EC / `platform.ini` blocks
  - AMI / ASUS / UEFI capsules
  - BIOS-region-only firmware volume runs
  - ZIP downloads, scanned recursively

  For containers it can't open (Dell PFS, 7-Zip/CAB self-extractors, encrypted HP updates) it explains what the file is and what to do.
- **ASUS CAP tool:** parses the capsule (GUID, header, flags, signature trailer) and reads the board and BIOS version from the file name and `$BOOTEFI$`. It strips the header and can merge the image with your dump so the board keeps its MAC.

Keyboard shortcuts: **F5** Detect · **F6** Read · **F4** Blank · **F9** Program · **F7** Verify · **F8** Auto · **Ctrl+O** Open · **Ctrl+S** Save · **Esc** Cancel

## Building from source

You need the [.NET 10 SDK](https://dot.net).

```sh
# run from source
dotnet run --project src/BiosFlashStudio.csproj               # add: -- --demo
```

**Windows**

```bat
build.bat              :: framework-dependent EXE        -> dist\
build.bat portable     :: self-contained single EXE      -> dist\portable\
build.bat installer    :: setup.exe (Inno Setup 6)        -> dist\installers\
build.bat linux        :: cross-builds linux-x64/arm64 + .tar.gz
```

**Linux** (Debian/Ubuntu or Fedora)

```sh
./build.sh             # .tar.gz + .deb (dpkg-deb) + .rpm (rpmbuild) for this machine
./build.sh arm64       # cross-build for ARM64
# package binaries already cross-built on Windows, no .NET SDK needed:
./packaging/linux/build-packages.sh x64 --prebuilt
```

On Fedora, install `rpm-build` to get `.rpm` output. On Debian/Ubuntu, `dpkg-deb` is already installed and `rpmbuild` comes with the `rpm` package.

## Testing

A headless self-test exercises everything in software. It runs these against simulated chips and a packet-level CH341A emulator:
- the analyzer
- the SPI and I²C drivers
- the raw USB protocol used by the WinUSB and libusb backends
- the capsule parser, the repair engine, the update extractor and the boot logo engine (UEFI parser, LZMA rebuild, conversions)

```sh
run-tests.bat           # or ./run-tests.sh   — full run with live progress bars
run-tests.bat --quick   # skips the 32 MB chip (~1.5 min)
run-tests.bat --repair  # repair tools, update extractor and boot logo (~3 s)
run-tests.bat --logo    # boot logo engine only (~2 s)
```

Progress is also written to `tests/SelfTest/bin/Release/net10.0/selftest.log`.

> **Note:** all automated tests run against simulated hardware. Please report results with real programmers and boards.

## Project layout

```
src/
  Hardware/    CH341 backends: WCH DLL, shared raw USB protocol, WinUSB + libusb transports, simulator, USB scanner
  Flash/       chip database, SPI NOR driver, I²C EEPROM driver
  Analysis/    firmware analyzer, UEFI volume parser, boot logo engine, capsule parser, repair engine, update extractor, demo image
  Controls/    hex editor, region bar, usage map, pinout drawings
  Ui/          theme palette, stroke icons, dialogs
  Platform/    OS helpers (udev rule install, folders)
  *.axaml      Avalonia UI
tests/SelfTest/  headless self-test
packaging/
  windows/     Inno Setup script
  linux/       .deb/.rpm/.tar.gz builder, install.sh, udev rule, desktop entry, icon
docs/          logo and screenshots
```

## Safety

> **Writing firmware can brick a board. You use this software at your own risk.**

- The classic black CH341A drives its data lines at **5 V**. Use a 3.3 V-modified board or a level shifter for 3.3 V chips.
- **1.8 V chips** (W25Q…FW/JW, MX25U…, GD25LQ…) need a 1.8 V adapter.
- **Always keep the original dump.** It is the only copy of the board's serial number, MAC, OEM key, ME configuration and settings.
- For ASUS USB BIOS FlashBack, keep the original `.CAP` with its header. Strip the header only for programmer use.
- BIOS password removal and other security-bypass features are intentionally **not** included.

## License

No license has been chosen yet. Until one is added, all rights are reserved by the author.

Third-party components: [Avalonia](https://avaloniaui.net) (MIT), [SkiaSharp](https://github.com/mono/SkiaSharp) (MIT) and [SharpCompress](https://github.com/adamhathcock/sharpcompress) (MIT, LZMA codec).

BiosFlash Studio is not affiliated with WCH, Intel, AMD, ASUS, Insyde or AMI. The CH341A USB protocol follows public documentation, such as the flashrom project. Product names are trademarks of their owners.
