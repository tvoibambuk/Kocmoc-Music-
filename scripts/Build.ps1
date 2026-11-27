[CmdletBinding()]
param([string]$WixDir, [switch]$PortableOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'dist'
$obj = Join-Path $root 'build'
New-Item -ItemType Directory -Force $out,$obj | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path $framework)) { $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$csc = Join-Path $framework 'csc.exe'
$sources = @(Get-ChildItem (Join-Path $root 'Core'),(Join-Path $root 'UI') -Filter '*.cs' -File | ForEach-Object FullName)
$sources += Join-Path $root 'packaging\AssemblyInfo.cs'
$refs = @('System.dll','System.Core.dll','System.Xaml.dll','System.Drawing.dll','System.Windows.Forms.dll','Microsoft.CSharp.dll',
    'WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$exe = Join-Path $out 'VeloraDisk.exe'
$icon = Join-Path $root 'assets\velora.ico'
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /warn:4 /warnaserror+ /main:Diskovy.Program "/out:$exe" "/win32icon:$icon" "/win32manifest:$root\packaging\Velora.manifest" "/resource:$root\UI\Main.xaml,Diskovy.Main.xaml" @refs @sources
if ($LASTEXITCODE -ne 0) { throw 'Failed to compile the application.' }
if ($PortableOnly) { Write-Output $exe; return }
if (-not $WixDir) {
    $WixDir = Join-Path $obj 'wix-3.14.1'
    if (-not (Test-Path (Join-Path $WixDir 'light.exe'))) {
        $zip = Join-Path $obj 'wix314-binaries.zip'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/wixtoolset/wix3/releases/download/wix3141rtm/wix314-binaries.zip' -OutFile $zip
        if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne '6AC824E1642D6F7277D0ED7EA09411A508F6116BA6FAE0AA5F2C7DAA2FF43D31') { throw 'The WiX toolset archive checksum does not match.' }
        Expand-Archive -LiteralPath $zip -DestinationPath $WixDir -Force
    }
}
$wixObject = Join-Path $obj 'Velora.wixobj'
& (Join-Path $WixDir 'candle.exe') -nologo "-dExe=$exe" "-dIcon=$icon" -out $wixObject (Join-Path $root 'packaging\Velora.wxs')
if ($LASTEXITCODE -ne 0) { throw 'Failed to compile the installer.' }
$msi = Join-Path $out 'VeloraDisk-Setup.msi'
& (Join-Path $WixDir 'light.exe') -nologo -sval -spdb -ext WixUIExtension -cultures:en-us -out $msi $wixObject
if ($LASTEXITCODE -ne 0) { throw 'Failed to link the installer.' }
$hashes = foreach ($file in $msi,$exe) { (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($file) }
[IO.File]::WriteAllLines((Join-Path $out 'SHA256SUMS.txt'), [string[]]$hashes)
Write-Output $msi
