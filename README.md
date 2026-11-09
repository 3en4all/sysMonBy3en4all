# sysMonBy3en4all

Lightweight Windows resource and server monitor (.NET 10, WPF), ~220 MB RAM, ~0.3% CPU.

![window](okno.png)

## Features
- CPU per physical core, process table (PID, path, CPU, RAM, sorting)
- GPU, RAM, disks, network (LibreHardwareMonitor, PDH)
- Local servers panel (detected from command lines; Ollama log view)
- Windows events panel with Polish descriptions
- Threshold alerts with hysteresis: CPU/GPU temperature, RAM, free disk space, SSD/NVMe health
- Uptime and last boot reason, tray icon, autostart, single instance

## Build
```
dotnet build -c Release
```
Requires Windows and .NET 10 SDK. Starts without administrator rights; temperatures, fans, power and SMART need administrator (Options → run as administrator) and the PawnIO driver (https://pawnio.eu).

MSIX package for Microsoft Store: `pakiet\buduj.ps1` (fill Identity in `pakiet\Package.appxmanifest` from Partner Center first). Third-party licenses: `pakiet\THIRD-PARTY-NOTICES.txt`.

Author: Jacek Grodnicki (3en4all)
