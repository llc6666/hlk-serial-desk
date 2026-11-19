$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot '../build/HLK-SerialDesk.exe'))
$captureType = $assembly.GetType('CaptureFiles', $true)
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$stem = $tempBase + '\HLK-Capture-Test-'
$testRoot = $stem + [Guid]::NewGuid().ToString('N')

function Assert([bool]$ok, [string]$message) { if (-not $ok) { throw $message } }

function Verify-Capture([string]$Label, [byte[]]$Payload, [string]$ExpectedText, [int]$Chunk) {
    $capture = [Activator]::CreateInstance($captureType, [object[]]@($testRoot, 'OFFLINE', 57600))
    try {
        $buffer = New-Object byte[] $Chunk
        for ($i = 0; $i -lt $Payload.Length; $i += $Chunk) {
            $count = [Math]::Min($Chunk, $Payload.Length - $i)
            [Array]::Copy($Payload, $i, $buffer, 0, $count)
            $capture.Receive($buffer, $count)
        }
        $capture.Finish('Offline transcript fixture')
        $folder = $capture.Folder
        $rawPath = Join-Path $folder 'rx.raw'
        $textPath = Join-Path $folder 'rx.txt'
        Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($rawPath)) -ceq [Convert]::ToBase64String($Payload)) "$Label raw differs."
        $bytes = [IO.File]::ReadAllBytes($textPath)
        Assert ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191) "$Label missing UTF-8 BOM."
        $actual = $utf8.GetString($bytes, 3, $bytes.Length - 3)
        Assert ($actual -ceq $ExpectedText) "$Label readable text differs."
        $sums = @{}
        foreach ($line in (Get-Content -LiteralPath (Join-Path $folder 'SHA256SUMS.txt'))) {
            Assert ($line -match '^([0-9a-f]{64})  (rx\.raw|rx\.txt|events\.log)$') "$Label malformed hash line."
            $sums.Add($Matches[2], $Matches[1])
        }
        Assert ($sums.Count -eq 3) "$Label hash manifest count differs."
        foreach ($item in $sums.GetEnumerator()) {
            Assert ((Get-FileHash -LiteralPath (Join-Path $folder $item.Key)).Hash -eq $item.Value) "$Label hash mismatch."
        }
        '{0}: PASS ({1:N0} raw bytes, {2:N0} readable UTF-8 bytes)' -f $Label, $Payload.Length, $bytes.Length
    } finally { $capture.Dispose() }
}

try {
    $cr = [string][char]13
    $lf = [string][char]10
    $esc = [string][char]27
    $line = $cr + $lf
    $inputText = ([string][char]0xFEFF) + '甲' + $cr + $lf + '乙' + $lf + $cr +
        '丙' + $cr + $cr + $lf + $cr + $lf + '丁' + $cr + '戊' + [char]9 +
        $esc + '[31m' + '红' + $esc + '[0m' + [char]8 + [char]0 + $cr
    $synthetic = $utf8.GetBytes($inputText)
    $syntheticText = '甲' + $line + '乙' + $line + '丙' + $line + $line + '丁' + $line + '戊' + [char]9 + '红' + $line
    Verify-Capture 'Byte-split Unicode, BOM, ANSI, controls and newlines' $synthetic $syntheticText 1
    $incomplete = [byte[]]($utf8.GetBytes('尾' + $cr) + [byte]0xE7)
    Verify-Capture 'Truncated UTF-8 at end of stream' $incomplete ('尾' + $line + [char]0xFFFD) 1
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolved = (Resolve-Path -LiteralPath $testRoot).Path
        if ($resolved -eq $testRoot -and $resolved.StartsWith($stem, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        } else {
            throw "Unexpected temporary output path: $resolved"
        }
    }
}
