<#
.SYNOPSIS
    Reads (and optionally writes) a Modbus TCP device from the command line.

.DESCRIPTION
    Exists because checking a register otherwise means retyping a socket client every time.
    No dependency: the framing is simple enough to do inline, which also keeps this usable on
    a machine with nothing installed.

    Addresses are WIRE addresses, 0-based. Weintek 4x-1 and Siemens' 1-based documentation both
    mean wire address 0, so subtract one from whatever the manual shows.

    Note the PLC at 192.0.2.10 runs MB_SERVER, which serves exactly ONE TCP connection - while
    the bridge is polling it, this script gets "connection refused". Read the bridge's own server
    instead, or stop the bridge first.

.EXAMPLE
    .\modbus-read.ps1 -Address 0 -Count 16
    .\modbus-read.ps1 -Address 200 -Count 67 -NonZero
    .\modbus-read.ps1 -Address 223 -Count 4 -Type uint32
    .\modbus-read.ps1 -Address 144 -Count 16 -Type string
    .\modbus-read.ps1 -Area discrete -Address 8 -Count 132 -NonZero
    .\modbus-read.ps1 -Target 192.0.2.10 -Area input -Address 3 -Count 4
    .\modbus-read.ps1 -Address 100 -WriteValue 1234        # single holding register
#>
[CmdletBinding()]
param(
    [string]$Target = '127.0.0.1',
    [int]$Port = 502,
    [ValidateSet('holding', 'input', 'coil', 'discrete')]
    [string]$Area = 'holding',
    [Parameter(Mandatory = $true)][int]$Address,
    [int]$Count = 1,
    [ValidateSet('uint16', 'int16', 'uint32', 'int32', 'float32', 'string')]
    [string]$Type = 'uint16',
    [byte]$Unit = 1,
    [double]$Gain = 1.0,
    [switch]$NonZero,
    [int]$WriteValue = [int]::MinValue,
    [int]$TimeoutMs = 2000
)

$ErrorActionPreference = 'Stop'

$functionCode = switch ($Area) {
    'coil'     { 1 }
    'discrete' { 2 }
    'holding'  { 3 }
    'input'    { 4 }
}

$client = New-Object System.Net.Sockets.TcpClient
try { $client.Connect($Target, $Port) }
catch { throw "Could not connect to ${Target}:${Port} - $($_.Exception.Message)" }

$stream = $client.GetStream()
$stream.ReadTimeout = $TimeoutMs

function Invoke-Modbus([byte]$fc, [int]$addr, [int]$value) {
    # MBAP header (txn, protocol 0, length, unit) followed by the PDU.
    $request = [byte[]](
        0x00, 0x01, 0x00, 0x00, 0x00, 0x06, $Unit, $fc,
        [byte](($addr -shr 8) -band 0xFF), [byte]($addr -band 0xFF),
        [byte](($value -shr 8) -band 0xFF), [byte]($value -band 0xFF))

    $stream.Write($request, 0, $request.Length)
    $stream.Flush()

    $buffer = New-Object byte[] 600
    $read = $stream.Read($buffer, 0, $buffer.Length)
    if ($read -lt 9) { throw "Short reply ($read bytes)." }

    # A function code with the high bit set is an exception response, not data.
    if ($buffer[7] -gt 0x80) {
        $meaning = switch ($buffer[8]) {
            1 { 'illegal function' }
            2 { 'illegal data address - outside any configured block' }
            3 { 'illegal data value' }
            4 { 'device failure - a block may be in its stale/failsafe state' }
            default { 'unknown' }
        }
        throw "Modbus exception $($buffer[8]) at address ${addr}: $meaning"
    }
    return @{ Buffer = $buffer; Length = $read }
}

try {
    if ($WriteValue -ne [int]::MinValue) {
        $writeFc = if ($Area -eq 'coil') { 5 } else { 6 }
        $payload = if ($Area -eq 'coil' -and $WriteValue -ne 0) { 0xFF00 } else { $WriteValue }
        Invoke-Modbus $writeFc $Address $payload | Out-Null
        Write-Host "wrote $WriteValue to $Area $Address" -ForegroundColor Green
        return
    }

    $reply = Invoke-Modbus $functionCode $Address $Count
    $buffer = $reply.Buffer
    $byteCount = $buffer[8]
    $data = $buffer[9..(8 + $byteCount)]

    if ($Area -in 'coil', 'discrete') {
        for ($i = 0; $i -lt $Count; $i++) {
            $bit = ($data[[int][Math]::Floor($i / 8)] -shr ($i % 8)) -band 1
            if ($NonZero -and $bit -eq 0) { continue }
            '{0,-8} {1}' -f ($Address + $i), $bit
        }
        return
    }

    # Registers are big-endian on the wire; 32-bit values are most-significant word first.
    $words = @()
    for ($i = 0; $i -lt $byteCount; $i += 2) { $words += [int]$data[$i] * 256 + [int]$data[$i + 1] }

    if ($Type -eq 'string') {
        $bytes = @()
        foreach ($w in $words) { $bytes += [byte](($w -shr 8) -band 0xFF); $bytes += [byte]($w -band 0xFF) }
        "'" + ([System.Text.Encoding]::UTF8.GetString($bytes)).Trim([char]0) + "'"
        return
    }

    $step = if ($Type -in 'uint32', 'int32', 'float32') { 2 } else { 1 }
    for ($i = 0; $i -lt $words.Count; $i += $step) {
        $raw = if ($step -eq 2) { [uint32]$words[$i] * 65536 + [uint32]$words[$i + 1] } else { [uint32]$words[$i] }

        $value = switch ($Type) {
            'int16'   { if ($raw -gt 32767) { [int]$raw - 65536 } else { [int]$raw } }
            'int32'   { if ($raw -gt 2147483647) { [int64]$raw - 4294967296 } else { [int64]$raw } }
            'float32' { [BitConverter]::ToSingle([BitConverter]::GetBytes([uint32]$raw), 0) }
            default   { $raw }
        }

        if ($NonZero -and $value -eq 0) { continue }
        if ($Gain -ne 1.0) { '{0,-8} {1,-14} {2}' -f ($Address + $i), $value, ($value * $Gain) }
        else { '{0,-8} {1}' -f ($Address + $i), $value }
    }
}
finally {
    $stream.Dispose()
    $client.Close()
}
