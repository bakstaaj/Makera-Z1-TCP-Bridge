# Makera Z1 TCP Bridge

A standalone Windows TCP bridge specifically for the **Makera Z1**. It connects gSender or another GRBL-style sender to the Z1 and translates individual commands, status messages, firmware settings, and Z1 sensor fields. It is separate from gSender and its plugins.

This bridge supports the Makera Z1 protocol. It does not currently support other CNC controller protocols.

![gSender connected to the Makera Z1 bridge at 127.0.0.1](screenshots/gsender-connected-to-local-bridge.png)

## Quick start

1. Download and extract `makera-z1-tcp-bridge-v0.3.12.zip`.
2. Open PowerShell **as Administrator**, change to the extracted folder, and run `Install-Z1Bridge.ps1`.
3. Enter the Z1 IP address and port when prompted. The usual Z1 port is `2222`.
4. In gSender, select **Ethernet** and connect to host `127.0.0.1`, port `2323`, using the GRBL profile.

The installer creates `C:\makera_z1_tcp_bridge`, writes `config.yaml`, and installs the `MakeraZ1TcpBridge` Windows service. These service and data-directory names are retained for in-place updates. The executable does not require a separate Python installation. YAML stores the Z1 connection, local listener, optional rotary setting, debug logging, and fallback GRBL profile.

## Supported Makera Z1 translations

- Forwards G-code commands and Z1 responses in both directions.
- Translates GRBL status and firmware-version requests.
- Reads the Z1 `/sd/config.txt` for GRBL settings and saves a local copy when available.
- Translates `$J=` jog commands, jog cancel, and GRBL Ctrl-X halt.
- Maps Z1 sensor fields to GRBL `Pn:` tokens where possible and preserves extended values in a `Z1:` status field.
- Polls Z1 diagnostics while gSender is connected to refresh accessory and sensor status.

The bridge translates individual commands. Probing, tool changes, file transfer, and job execution remain the responsibility of the sender and are sent as G-code/controller commands.

## Configuration and troubleshooting

The installed configuration is `C:\makera_z1_tcp_bridge\config.yaml`. The service binds to `127.0.0.1`, so it accepts sender connections from this computer, not other network devices.

Set `logging.debug: true` and restart the service to record command and response frames under `C:\makera_z1_tcp_bridge\logs`. Turn debug logging off after troubleshooting to reduce log volume.

```powershell
Get-Service MakeraZ1TcpBridge
Restart-Service MakeraZ1TcpBridge
```

The bridge downloads the Z1’s `/sd/config.txt` at connection time and saves a copy in `C:\makera_z1_tcp_bridge\config.txt` when available. The YAML `grbl-settings` block is used only as a fallback and is not written to the Z1.

Set `rotary.installed: true` only when the optional A-axis module is attached, then restart the service and reconnect gSender. The module is not detected automatically.

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

Protocol translation is based on captured Makera Studio traffic and the published Z1 firmware source. gSender connection, basic commands, jogs, status fields, config transfer, and Windows service installation have been exercised during development. A successful package build does not prove every machine state, sensor, or complete job has been validated. Supervise motion and verify sensor mappings on the actual Z1.

## Package contents

The ZIP includes the prebuilt bridge executable, NSSM service wrapper and notice, installer, update/uninstall scripts, C# source, build/package scripts, README, and screenshot.