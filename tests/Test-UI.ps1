[CmdletBinding()]
param([string]$PreviewPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path $framework)) { $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$temp = [IO.Path]::GetTempPath()
$scratch = Join-Path $temp ('VeloraUiBuild-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $binary = Join-Path $scratch 'UiTests.exe'
    $sources = @(Get-ChildItem (Join-Path $root 'Core'),(Join-Path $root 'UI') -Filter '*.cs' -File | ForEach-Object FullName)
    $sources += Join-Path $PSScriptRoot 'UiTests.cs'
    $refs = @('System.dll','System.Core.dll','Microsoft.CSharp.dll','System.Xaml.dll','System.Drawing.dll','System.Windows.Forms.dll',
        'WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
    & (Join-Path $framework 'csc.exe') /nologo /target:exe /warnaserror+ /codepage:65001 /main:Diskovy.UiTests "/resource:$root\UI\Main.xaml,Diskovy.Main.xaml" "/win32icon:$root\assets\velora.ico" "/out:$binary" @refs @sources
    if ($LASTEXITCODE -ne 0) { throw 'UI test compilation failed' }
    if ($PreviewPath) { & $binary ([IO.Path]::GetFullPath($PreviewPath)) }
    else { & $binary }
    if ($LASTEXITCODE -ne 0) { throw 'UI tests failed' }
} finally {
    $resolved = (Resolve-Path -LiteralPath $scratch).Path
    if ([IO.Path]::GetDirectoryName($resolved).TrimEnd('\') -ieq $temp.TrimEnd('\') -and
        [IO.Path]::GetFileName($resolved) -match '^VeloraUiBuild-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
