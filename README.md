# Makera Z1 TCP Bridge

A standalone Windows service that connects gSender’s built-in Ethernet client to the Makera Z1. The bridge translates individual commands and status messages between gSender’s GRBL-style TCP connection and the Z1’s native TCP protocol. It runs separately from gSender and its plugins.

![gSender connected to the local bridge at 127.0.0.1](screenshots/gsender-connected-to-local-bridge.png)

## Quick start

1. Download and extract `makera-z1-tcp-bridge-v0.3.10.zip`.
2. Open PowerShell **as Administrator**, change to the extracted folder, and run `Install-Z1Bridge.ps1`.
3. Enter the Z1 IP address and port when prompted. The usual Z1 port is `2222`.
4. In gSender, select **Ethernet** and connect to host `127.0.0.1`, port `2323`.

The installer creates `C:\makera_z1_tcp_bridge`, writes `config.yaml`, and installs the automatic `MakeraZ1TcpBridge` Windows service. The included executable does not require a separate Python installation. The YAML file stores the Z1 address, local listener, optional rotary setting, debug logging, and fallback GRBL profile.

## What it translates

- G-code commands and Z1 responses in both directions.
- gSender real-time status requests to the Z1 status request, with GRBL-compatible status responses.
- gSender `$I` firmware queries to the Z1 firmware-version request.
- gSender `$$` settings requests from a read-only `/sd/config.txt` transfer on the Z1, with YAML fallback settings if the transfer fails.
- gSender `$J=` jog commands into equivalent absolute moves that the Z1 accepts, plus real-time jog cancel.
- gSender Ctrl-X to the Z1 halt byte.
- Z1 sensor fields into standard GRBL `Pn:` tokens where possible, while preserving Makera-only sensor vectors in the `Z1:` status field.
- Studio’s 700 ms diagnostics polling while gSender is connected, which keeps the Z1 accessory controls and sensor fields refreshed.

The bridge forwards individual commands. Probing, tool changes, file transfer, and job execution remain in gSender and are sent as ordinary G-code; the bridge does not add a separate machining workflow.

## Configuration and troubleshooting

The installed configuration is `C:\makera_z1_tcp_bridge\config.yaml`. The service binds only to `127.0.0.1`, so it accepts gSender connections from this computer, not other network devices.

Set `logging.debug: true` and restart the service to record command and response frames under `C:\makera_z1_tcp_bridge\logs`. Turn debug logging off after troubleshooting to reduce log volume.

```powershell
Get-Service MakeraZ1TcpBridge
Restart-Service MakeraZ1TcpBridge
```

The bridge downloads the Z1’s `/sd/config.txt` at connection time and saves a copy in `C:\makera_z1_tcp_bridge\config.txt` when available. The YAML `grbl-settings` block is used only as a fallback and is not written to the machine.

Set `rotary.installed: true` only when the optional A-axis module is attached, then restart the service and reconnect gSender. The bridge does not detect the physical module automatically.

## Update and uninstall

For an in-place update, run `Update-InstalledService.ps1` from an elevated PowerShell window. It replaces the executable and restarts the service while preserving the YAML configuration and logs.

To remove the Windows service, run `Uninstall-Z1Bridge.ps1` as Administrator. The data directory, configuration, and logs are retained.

## Build from source

The optional build uses the Windows .NET Framework C# compiler included with supported Windows versions. It does not need a Python install or a .NET SDK.

```powershell
.\Build-Bridge.ps1
.\Package-Bridge.ps1
```

`Package-Bridge.ps1` creates the standalone ZIP in the repository root.

## Protocol and hardware validation

Protocol translation is based on captured Makera Studio traffic and the published Z1 firmware source. gSender connection, basic commands, jogs, status fields, config transfer, and bridge service installation have been exercised during development. A successful package build is not proof that every machine state, sensor, or complete CNC job has been validated. Supervise machine motion and confirm sensor mappings on the actual machine.

## Package contents

The ZIP includes the prebuilt bridge executable, NSSM service wrapper and notice, installer, update/uninstall scripts, C# source, build/package scripts, README, and screenshot.
