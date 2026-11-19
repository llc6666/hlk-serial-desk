$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc)) { throw '.NET Framework C# compiler could not be found.' }
$dest = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Path $dest -Force | Out-Null
$exe = Join-Path $dest 'HLK-SerialDesk.exe'
$files = 'AssemblyInfo.cs', 'SerialDesk.cs', 'LiveSerial.cs', 'CaptureFiles.cs', 'SerialView.cs', 'DeskStyle.cs' | ForEach-Object {
    Join-Path (Join-Path $PSScriptRoot 'src') $_
}
& $csc '/nologo' '/target:winexe' '/optimize+' '/langversion:5' '/warnaserror+' '/reference:System.Windows.Forms.dll' '/reference:System.Drawing.dll' '/reference:System.Management.dll' "/out:$exe" $files
if ($LASTEXITCODE -ne 0) { throw "csc exited with code $LASTEXITCODE." }
Get-FileHash -LiteralPath $exe -Algorithm SHA256 | Format-List Algorithm,Hash,Path
