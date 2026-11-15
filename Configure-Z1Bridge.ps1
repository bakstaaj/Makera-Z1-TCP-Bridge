# Interactive connection settings for an installed Makera Z1 TCP Bridge.
$ErrorActionPreference = 'Stop'
$ServiceName = 'MakeraZ1TcpBridge'
$InstallDir = 'C:\makera_z1_tcp_bridge'
$ConfigPath = Join-Path $InstallDir 'config.yaml'
$DiscoveryScript = Join-Path $InstallDir 'Discover-Z1.ps1'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"'
    $powerShellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $powerShellExe)) { throw "Windows PowerShell was not found: $powerShellExe" }
    Start-Process -FilePath $powerShellExe -ArgumentList $arguments -Verb RunAs
    exit
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Makera Z1 TCP Bridge Settings'
$form.StartPosition = 'CenterScreen'
$form.Size = New-Object System.Drawing.Size(500, 300)
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$form.MinimizeBox = $false

$ipLabel = New-Object System.Windows.Forms.Label
$ipLabel.Text = 'Makera Z1 IP address'
$ipLabel.Location = New-Object System.Drawing.Point(20, 22)
$ipLabel.AutoSize = $true
$form.Controls.Add($ipLabel)

$ipBox = New-Object System.Windows.Forms.TextBox
$ipBox.Location = New-Object System.Drawing.Point(190, 18)
$ipBox.Size = New-Object System.Drawing.Size(260, 24)
$form.Controls.Add($ipBox)

$portLabel = New-Object System.Windows.Forms.Label
$portLabel.Text = 'Makera Z1 TCP port'
$portLabel.Location = New-Object System.Drawing.Point(20, 58)
$portLabel.AutoSize = $true
$form.Controls.Add($portLabel)

$portBox = New-Object System.Windows.Forms.TextBox
$portBox.Location = New-Object System.Drawing.Point(190, 54)
$portBox.Size = New-Object System.Drawing.Size(90, 24)
$form.Controls.Add($portBox)

$discoverButton = New-Object System.Windows.Forms.Button
$discoverButton.Text = 'Discover Z1'
$discoverButton.Location = New-Object System.Drawing.Point(20, 96)
$discoverButton.Size = New-Object System.Drawing.Size(120, 30)
$form.Controls.Add($discoverButton)

$devices = New-Object System.Windows.Forms.ComboBox
$devices.Location = New-Object System.Drawing.Point(150, 99)
$devices.Size = New-Object System.Drawing.Size(300, 25)
$devices.DropDownStyle = 'DropDownList'
$devices.DisplayMember = 'Label'
$form.Controls.Add($devices)

$status = New-Object System.Windows.Forms.Label
$status.Location = New-Object System.Drawing.Point(20, 140)
$status.Size = New-Object System.Drawing.Size(440, 42)
$status.Text = 'Current settings are loaded from the installed bridge configuration.'
$form.Controls.Add($status)

$saveButton = New-Object System.Windows.Forms.Button
$saveButton.Text = 'Save and restart service'
$saveButton.Location = New-Object System.Drawing.Point(230, 200)
$saveButton.Size = New-Object System.Drawing.Size(220, 34)
$form.Controls.Add($saveButton)

$cancelButton = New-Object System.Windows.Forms.Button
$cancelButton.Text = 'Close'
$cancelButton.Location = New-Object System.Drawing.Point(20, 200)
$cancelButton.Size = New-Object System.Drawing.Size(100, 34)
$cancelButton.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
$form.Controls.Add($cancelButton)
$form.CancelButton = $cancelButton

try {
    if (-not (Test-Path -LiteralPath $ConfigPath)) { throw "Bridge configuration not found: $ConfigPath" }
    $configText = [System.IO.File]::ReadAllText($ConfigPath)
    if ($configText -notmatch '(?m)^  host:\s*([^\s#]+)') { throw 'Could not read z1.host from config.yaml.' }
    $ipBox.Text = $Matches[1]
    if ($configText -notmatch '(?m)^  port:\s*(\d+)') { throw 'Could not read z1.port from config.yaml.' }
    $portBox.Text = $Matches[1]
} catch {
    [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Makera Z1 TCP Bridge', 'OK', 'Error') | Out-Null
    exit 1
}

$discoverButton.Add_Click({
    $discoverButton.Enabled = $false
    $status.Text = 'Listening for Makera Z1 discovery beacons on UDP 3333 for 5 seconds...'
    $form.Refresh()
    try {
        if (-not (Test-Path -LiteralPath $DiscoveryScript)) { throw "Discovery script not found: $DiscoveryScript" }
        . $DiscoveryScript
        $found = @(Find-MakeraZ1 -ListenSeconds 5)
        $devices.Items.Clear()
        foreach ($device in $found) {
            $devices.Items.Add([pscustomobject]@{
                Label = "$($device.Model)  $($device.IPAddress):$($device.TcpPort)  [$($device.Status)]"
                IPAddress = $device.IPAddress
                TcpPort = $device.TcpPort
            }) | Out-Null
        }
        if ($found.Count -eq 0) {
            $status.Text = 'No Z1 beacons found. You can enter the IP address and port manually.'
        } else {
            $devices.SelectedIndex = 0
            $status.Text = "Found $($found.Count) Z1 machine(s). Select one, or edit the address and port manually."
        }
    } catch {
        $status.Text = 'Discovery failed: ' + $_.Exception.Message
    } finally {
        $discoverButton.Enabled = $true
    }
})

$devices.Add_SelectedIndexChanged({
    $device = $devices.SelectedItem
    if ($null -ne $device) {
        $ipBox.Text = $device.IPAddress
        $portBox.Text = [string]$device.TcpPort
    }
})

$saveButton.Add_Click({
    $address = $null
    $port = 0
    if (-not [System.Net.IPAddress]::TryParse($ipBox.Text.Trim(), [ref]$address) -or
        $address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        [System.Windows.Forms.MessageBox]::Show('Enter a valid IPv4 address.', 'Invalid address', 'OK', 'Warning') | Out-Null
        return
    }
    if (-not [int]::TryParse($portBox.Text.Trim(), [ref]$port) -or $port -lt 1 -or $port -gt 65535) {
        [System.Windows.Forms.MessageBox]::Show('Enter a TCP port from 1 to 65535.', 'Invalid port', 'OK', 'Warning') | Out-Null
        return
    }

    $saveButton.Enabled = $false
    $status.Text = 'Saving connection settings and restarting the bridge service...'
    $form.Refresh()
    $tempPath = $ConfigPath + '.new'
    $backupPath = $ConfigPath + '.previous'
    try {
        $hostPattern = [regex]::new('(?m)^(  host:[ \t]*)[^\r\n]+')
        $portPattern = [regex]::new('(?m)^(  port:[ \t]*)\d+[^\r\n]*')
        $newConfig = $hostPattern.Replace($configText, ('${1}' + $address.ToString()), 1)
        $newConfig = $portPattern.Replace($newConfig, ('${1}' + $port), 1)
        if ($newConfig -notmatch ('(?m)^  host:[ \t]*' + [regex]::Escape($address.ToString()) + '[ \t]*\r?$') -or
            $newConfig -notmatch ('(?m)^  port:[ \t]*' + $port + '[ \t]*\r?$') -or
            $newConfig -notmatch '(?m)^  host:[ \t]*127\.0\.0\.1[ \t]*\r?$' -or
            $newConfig -notmatch '(?m)^  port:[ \t]*2323[ \t]*\r?$') {
            throw 'Could not safely update only the Z1 host and port in config.yaml.'
        }
        [System.IO.File]::WriteAllText($tempPath, $newConfig, [System.Text.UTF8Encoding]::new($false))

        $service = Get-Service -Name $ServiceName -ErrorAction Stop
        $previousListenerProcessIds = @(Get-NetTCPConnection -State Listen -LocalAddress 127.0.0.1 -LocalPort 2323 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
        [System.IO.File]::Replace($tempPath, $ConfigPath, $backupPath, $true)
        $startError = $null
        try { Start-Service -Name $ServiceName -ErrorAction Stop }
        catch { $startError = $_.Exception.Message }

        $startupConfirmed = $false
        $listenerProcessId = $null
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            $service = Get-Service -Name $ServiceName
            if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running) {
                $listeners = @(Get-NetTCPConnection -State Listen -LocalAddress 127.0.0.1 -LocalPort 2323 -ErrorAction SilentlyContinue)
                foreach ($listener in $listeners) {
                    $process = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
                    if ($null -eq $process -or $process.ProcessName -ne 'MakeraZ1TcpBridge') { continue }
                    if ($previousListenerProcessIds -contains $listener.OwningProcess) { continue }
                    Start-Sleep -Milliseconds 300
                    $service.Refresh()
                    $stillListening = @(Get-NetTCPConnection -State Listen -LocalAddress 127.0.0.1 -LocalPort 2323 -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -eq $listener.OwningProcess })
                    if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running -and $stillListening.Count -gt 0) {
                        $startupConfirmed = $true
                        $listenerProcessId = $listener.OwningProcess
                        break
                    }
                }
            }
            if ($startupConfirmed) { break }
            Start-Sleep -Milliseconds 300
        } while ([DateTime]::UtcNow -lt $deadline)

        if (-not $startupConfirmed) {
            $detail = "Bridge service did not start a new listener on 127.0.0.1:2323 for Z1 at $($address.ToString()):$port."
            if ($startError) { $detail += " Service start: $startError" }
            $detail += " Service state: $($service.Status)."
            throw $detail
        }
        Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
        $configText = $newConfig
        $status.Text = "Bridge service restarted with Z1 at $($address.ToString()):$port (process $listenerProcessId)."
        [System.Windows.Forms.MessageBox]::Show($status.Text, 'Settings applied', 'OK', 'Information') | Out-Null
        $form.Close()
    } catch {
        if (Test-Path -LiteralPath $backupPath) {
            try {
                if ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status -ne 'Stopped') {
                    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
                }
                [System.IO.File]::Copy($backupPath, $ConfigPath, $true)
                Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
            } catch { }
        }
        Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath ($ConfigPath + '.replace') -Force -ErrorAction SilentlyContinue
        $status.Text = 'Could not apply settings: ' + $_.Exception.Message
        [System.Windows.Forms.MessageBox]::Show($status.Text, 'Settings not applied', 'OK', 'Error') | Out-Null
    } finally {
        $saveButton.Enabled = $true
    }
})

[void]$form.ShowDialog()
