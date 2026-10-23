# CNC Machine TCP Bridge

A standalone Windows TCP service that connects a GRBL-style sender to a CNC controller protocol adapter. It runs independently from gSender and its plugins. The current adapter supports the **Makera Z1**; the bridge can serve as a base for additional controller adapters.

![gSender connected to the local bridge at 127.0.0.1](screenshots/gsender-connected-to-local-bridge.png)

## Quick start: Makera Z1

1. Download and extract `cnc-machine-tcp-bridge-v0.3.11.zip`.
2. Open PowerShell **as Administrator**, change to the extracted folder, and run `Install-Z1Bridge.ps1`.
3. Enter the Z1 IP address and port when prompted. The usual Z1 port is `2222`.
4. In gSender, select **Ethernet** and connect to host `127.0.0.1`, port `2323`, using the GRBL profile.

The installer creates `C:\makera_z1_tcp_bridge`, writes `config.yaml`, and installs the `MakeraZ1TcpBridge` Windows service. These legacy service and data-directory names are retained for in-place updates. The executable does not require a separate Python installation. YAML stores the Z1 connection, local listener, optional rotary setting, debug logging, and fallback GRBL profile.

## Current Z1 adapter

- Forwards G-code commands and Z1 responses in both directions.
- Translates GRBL status and firmware-version requests.
- Reads the Z1 `/sd/config.txt` for GRBL settings and saves a local copy when available.
- Translates `$J=` jog commands, jog cancel, and GRBL Ctrl-X halt.
- Maps Z1 sensor fields to GRBL `Pn:` tokens where possible and preserves extended values in a `Z1:` status field.
- Polls Z1 diagnostics while gSender is connected to refresh accessory and sensor status.

The bridge translates individual commands. Probing, tool changes, file transfer, and job execution remain the responsibility of the sender. The current controller-specific protocol adapter is Makera Z1 only; other machines require a compatible adapter and matching settings or profile.

## Configuration and troubleshooting

The installed configuration is `C:\makera_z1_tcp_bridge\config.yaml`. The service binds to `127.0.0.1`, so it accepts sender connections from this computer, not other network devices.

Set `logging.debug: true` and restart the service to record command and response frames under `C:\makera_z1_tcp_bridge\logs`. Turn debug logging off after troubleshooting to reduce log volume.

```powershell
Get-Service MakeraZ1TcpBridge
Restart-Service MakeraZ1TcpBridge
```

The bridge downloads the Z1’s `/sd/config.txt` at connection time and saves a copy in `C:\makera_z1_tcp_bridge\config.txt` when available. The YAML `grbl-settings` block is used only as a fallback and is not written to the controller.

Set `rotary.installed: true` only when the optional A-axis module is attached, then restart the service and reconnect the sender. The module is not detected automatically.

## Update and uninstall

For an in-place update, run `Update-InstalledService.ps1` from an elevated PowerShell window. It replaces the executable and restarts the service while preserving YAML configuration and logs.

To remove the Windows service, run `Uninstall-Z1Bridge.ps1` as Administrator. The data directory, configuration, and logs are retained.

## Build from source

The optional build uses the Windows .NET Framework C# compiler included with supported Windows versions. It does not need a Python installation or a .NET SDK.

```powershell
.\Build-Bridge.ps1
.\Package-Bridge.ps1
```

`Package-Bridge.ps1` creates the standalone ZIP in the repository root.

## Protocol and validation

The Z1 adapter is based on captured Makera Studio traffic and published Z1 firmware source. gSender connection, basic commands, jogs, status fields, config transfer, and Windows service installation have been exercised during development. A successful package build does not prove every machine state, sensor, or complete job has been validated. Supervise motion and verify sensor mappings on the target machine.

## Package contents

The ZIP includes the prebuilt bridge executable, NSSM service wrapper and notice, installer, update/uninstall scripts, C# source, build/package scripts, README, and screenshot.
