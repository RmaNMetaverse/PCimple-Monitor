# PCimple Monitor
https://github.com/user-attachments/assets/d9e2908c-ff3e-44d0-bc70-9658aeb5eb1c

PCimple Monitor shows session average and peak temperatures for CPU packages, GPUs, and physical drives. It also shows drive health and battery capacity retained. Close the dashboard to keep sampling from the notification area, or open the compact desktop widget from the dashboard or tray.

## Screenshots

### Dashboard

![PCimple Monitor dashboard showing average and peak CPU, Intel and NVIDIA GPU, and NVMe drive temperatures, plus drive and battery health](docs/screenshots/dashboard.jpg)

### Desktop widget

![Compact desktop widget showing the same session readings, with a Pin option to stay above other windows](docs/screenshots/widget.jpg)

## Install

Use the installer for your Windows architecture from [GitHub Releases](https://github.com/RmaNMetaverse/PCimple-Monitor/releases). PCimple Monitor targets Windows 10 22H2 and Windows 11. Setup installs a local sensor service and the dashboard runs as your Windows user. The x64 installer includes the official signed PawnIO setup, so compatible Intel and AMD PCs can enable CPU sensing offline. To repair a failed driver setup in version 1.0.0, run the newer x64 installer over the existing copy and keep CPU temperature sensing selected.

Sensor coverage depends on firmware, drivers, and storage controllers. On x86 and ARM64, CPU temperature is currently unavailable unless a verified package source is added for the platform. NVAPI supports native ARM64 for NVIDIA GPUs. An unavailable sensor is shown as such. Some USB and RAID controllers expose limited physical-drive health data.

On x64, Intel graphics can use Intel driver telemetry. When the driver omits it, the sensor service can read the processor's separate graphics temperature on supported Raptor Lake mobile systems with PawnIO.


## Hardware validation

For each target architecture, check install, upgrade, uninstall, standard-user launch, tray, widget, and Reset statistics. Verify the CPU package reading on Intel and AMD x64 hardware with PawnIO. Check NVMe SMART and SATA drive health against the same physical drive in a vendor utility. Confirm hybrid GPU enumeration, battery capacity, sleep/resume, and light and dark themes. Run the ARM64 package on RTX Spark before listing its sensor coverage as verified.
