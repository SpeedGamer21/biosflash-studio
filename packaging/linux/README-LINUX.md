# BiosFlash Studio on Linux (Debian / Ubuntu / Fedora)

## Install

**Package (recommended)**

```sh
# Debian / Ubuntu / Mint
sudo apt install ./biosflash-studio_2.1.0_amd64.deb

# Fedora / RHEL / Rocky
sudo dnf install ./biosflash-studio-2.1.0-1.x86_64.rpm
```

**Portable folder or tar.gz** (any distro)

```sh
tar xzf biosflash-studio-2.1.0-linux-x64.tar.gz
cd biosflash-studio-2.1.0
sudo sh install.sh            # installs to /opt, adds the udev rule + menu entry
# or run without installing:
chmod +x BiosFlashStudio && ./BiosFlashStudio
```

Then **re-plug the CH341A** so the udev rule applies. After that, no root rights are needed.

## Requirements

| | Debian / Ubuntu | Fedora |
|---|---|---|
| USB access | `libusb-1.0-0` | `libusb1` |
| GUI | `libfontconfig1 libx11-6 libice6 libsm6` | `fontconfig libX11 libICE libSM` |

**No kernel driver is needed.** The app talks to the CH341A (`1a86:5512`) from user space through libusb.
The in-kernel `ch341` module only serves the UART mode (`1a86:7523`). If `lsusb` shows `7523`, the jumper is
on the serial position: move it to pins 1-2 (SPI/I²C) and re-plug.

The .NET runtime is bundled. The app runs on X11 and on Wayland desktops through XWayland (GNOME and KDE ship it by default).

## Permissions (udev)

Without the udev rule only root can talk to the programmer. The packages and `install.sh` add
`60-biosflash-ch341a.rules`. You can also add it from the app: **Diagnostics → Install udev rule**.
To add it manually:

```sh
echo 'SUBSYSTEM=="usb", ATTRS{idVendor}=="1a86", ATTRS{idProduct}=="5512", MODE="0666", TAG+="uaccess"' | \
  sudo tee /etc/udev/rules.d/60-biosflash-ch341a.rules
sudo udevadm control --reload-rules && sudo udevadm trigger
```

## Troubleshooting

* `lsusb` should list `1a86:5512 QinHeng Electronics CH341 in EPP/MEM/I2C mode`.
  If it shows `1a86:5523` / `7523` instead, the programmer is in UART mode: move the jumper to pins 1-2.
* If a kernel module (`spi-ch341`, `i2c-ch341`) claims the device, the app detaches it automatically.
* Close flashrom or other tools that use the programmer — the USB interface can only be claimed once.
* Settings and logs: `~/.config/BiosFlashStudio/`. Backups: `~/Documents/BiosFlash Studio/Backups`.

## Build from source

```sh
# .NET 10 SDK required
./packaging/linux/build-packages.sh          # x64
./packaging/linux/build-packages.sh arm64    # Raspberry Pi 4/5, ARM laptops
```
