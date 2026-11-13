# Listen for Makera Z1 discovery beacons sent to UDP port 3333.
# The controller advertises periodically, so no address-range scan is needed.
function Find-MakeraZ1 {
    param(
        [ValidateRange(1, 60)]
        [int]$ListenSeconds = 5
    )

    $udp = [System.Net.Sockets.UdpClient]::new()
    $devices = @{}
    try {
        $udp.Client.ExclusiveAddressUse = $false
        $udp.Client.SetSocketOption(
            [System.Net.Sockets.SocketOptionLevel]::Socket,
            [System.Net.Sockets.SocketOptionName]::ReuseAddress,
            $true
        )
        $udp.Client.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 3333))
        $udp.Client.ReceiveTimeout = 500

        $deadline = [DateTime]::UtcNow.AddSeconds($ListenSeconds)
        while ([DateTime]::UtcNow -lt $deadline) {
            $remote = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
            try {
                $bytes = $udp.Receive([ref]$remote)
            } catch [System.Net.Sockets.SocketException] {
                if ($_.Exception.SocketErrorCode -in @(
                    [System.Net.Sockets.SocketError]::TimedOut,
                    [System.Net.Sockets.SocketError]::WouldBlock
                )) { continue }
                throw
            }

            $payload = [System.Text.Encoding]::UTF8.GetString($bytes).Trim([char]0, [char]13, [char]10, ' ')
            $fields = @($payload.Split(',') | ForEach-Object { $_.Trim().Trim('"') })
            if ($fields.Count -lt 5 -or $fields[0] -notmatch '(?i)^Makera[_ ]?Z1') { continue }

            $address = $remote.Address.ToString()
            $advertisedAddress = $null
            if ([System.Net.IPAddress]::TryParse($fields[1], [ref]$advertisedAddress) -and
                $advertisedAddress.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork) {
                $address = $advertisedAddress.ToString()
            }

            $tcpPort = 2222
            $parsedPort = 0
            if ([int]::TryParse($fields[2], [ref]$parsedPort) -and $parsedPort -ge 1 -and $parsedPort -le 65535) {
                $tcpPort = $parsedPort
            }

            $devices[$address] = [pscustomobject]@{
                Model = 'Makera Z1'
                IPAddress = $address
                TcpPort = $tcpPort
                Status = $fields[4]
                Details = $payload
            }
        }
    } finally {
        $udp.Dispose()
    }

    return @($devices.Values | Sort-Object IPAddress)
}

if ($MyInvocation.InvocationName -ne '.') {
    $found = Find-MakeraZ1
    if ($found.Count -eq 0) {
        Write-Host 'No Makera Z1 discovery beacons received on UDP 3333.' -ForegroundColor Yellow
        exit 1
    }
    $found | Format-Table Model, IPAddress, TcpPort, Status -AutoSize
    Write-Host 'Beacon details:'
    $found | ForEach-Object { Write-Host "$($_.IPAddress): $($_.Details)" }
}
