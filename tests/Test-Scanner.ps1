[CmdletBinding()]
param([string]$FixtureParent = $PSScriptRoot, [switch]$ExerciseRecycle)

$ErrorActionPreference = 'Stop'
function Check([bool]$condition, [string]$description) {
    if (-not $condition) { throw "Failed: $description" }
    Write-Host "PASS $description"
}

$core = Join-Path (Split-Path -Parent $PSScriptRoot) 'Core'
$parent = (Resolve-Path -LiteralPath $FixtureParent).Path
$fixture = Join-Path $parent ('diskovy-test-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $fixture 'files'
$nested = Join-Path $data 'nested'
$junction = Join-Path $data 'linked'
$token = [Threading.CancellationToken]::None

try {
    [void][IO.Directory]::CreateDirectory($nested)
    Add-Type -Path @((Join-Path $core 'Scan.cs'), (Join-Path $core 'FileActions.cs'), (Join-Path $core 'Recycle.cs'))
    [IO.File]::WriteAllBytes((Join-Path $data 'alpha.bin'), (New-Object 'byte[]' 30))
    [IO.File]::WriteAllBytes((Join-Path $data 'beta.bin'), (New-Object 'byte[]' 30))
    [IO.File]::WriteAllBytes((Join-Path $nested 'large.bin'), (New-Object 'byte[]' 42))
    [IO.File]::WriteAllBytes((Join-Path $data 'zero.bin'), [byte[]]@())
    [void](New-Item -ItemType Junction -Path $junction -Target $nested)

    $roots = [string[]]@($nested, $data, $data, (Join-Path $fixture 'missing'))
    $result = [Diskovy.DiskScanner]::Scan($roots, 30, 2, $token, $null)
    Check ($result.FilesSeen -eq 4 -and $result.BytesSeen -eq 102 -and $result.Matches -eq 3) 'overlapping roots and inclusive threshold'
    Check ($result.Truncated -and (($result.Files.Name -join ',') -eq 'large.bin,alpha.bin')) 'bounded ranking with deterministic ties'
    Check ($result.Skipped -eq 1 -and $result.ReparseSkipped -eq 1) 'inaccessible roots and junctions are counted separately'
    $viaLink = [Diskovy.DiskScanner]::Scan([string[]]@($junction), 0, 2, $token, $null)
    Check ($viaLink.FilesSeen -eq 0 -and $viaLink.ReparseSkipped -eq 1) 'junction root is not traversed'
    $cancel = New-Object Threading.CancellationTokenSource
    try {
        $progress = [Action[Diskovy.ScanProgress]] { param($snapshot) $cancel.Cancel() }
        $partial = [Diskovy.DiskScanner]::Scan([string[]]@($data), 0, 2, $cancel.Token, $progress)
        Check ($partial.Cancelled -and $partial.FilesSeen -eq 0) 'cancellation returns a partial result'
    } finally { $cancel.Dispose() }

    $sample = $result.Files[0]
    [IO.File]::AppendAllText($sample.Path, 'x')
    $refused = $false
    try { [Diskovy.FileActions]::Recycle($sample) } catch { $refused = $true }
    Check ($refused -and [IO.File]::Exists($sample.Path)) 'changed scan result is preserved'
    $sample = [Diskovy.DiskScanner]::Scan([string[]]@($nested), 0, 2, $token, $null).Files[0]
    $replacement = Join-Path $fixture 'original'
    [IO.Directory]::Move($nested, $replacement)
    [void](New-Item -ItemType Junction -Path $nested -Target $replacement)
    try {
        $refused = $false
        try { [Diskovy.FileActions]::Recycle($sample) } catch { $refused = $true }
        Check ($refused -and [IO.File]::Exists($sample.Path)) 'a replaced parent directory is preserved'
    } finally { [IO.Directory]::Delete($nested); [IO.Directory]::Move($replacement, $nested) }

    $protectedPath = Join-Path $data 'system.bin'
    [IO.File]::WriteAllText($protectedPath, 'fixture')
    [IO.File]::SetAttributes($protectedPath, [IO.FileAttributes]::System)
    $protectedEntry = [Diskovy.DiskScanner]::Scan([string[]]@($data), 0, 20, $token, $null).Files | Where-Object Name -eq 'system.bin'
    $refused = $false
    try { [Diskovy.FileActions]::Recycle($protectedEntry) } catch { $refused = $true }
    Check ($refused -and -not $protectedEntry.CanRecycle -and [IO.File]::Exists($protectedPath)) 'system-marked files are protected'
    [IO.File]::SetAttributes($protectedPath, [IO.FileAttributes]::Normal)

    $appDirectory = Join-Path $data 'AppData'
    [void][IO.Directory]::CreateDirectory($appDirectory)
    [IO.File]::WriteAllText((Join-Path $appDirectory 'cache.db'), 'fixture')
    $appFile = [Diskovy.DiskScanner]::Scan([string[]]@($appDirectory), 0, 2, $token, $null).Files[0]
    Check (-not $appFile.CanRecycle) 'application-data files remain visible and protected'

    $first = $fixture + '\' + ('a' * 115)
    $deep = $first + '\' + ('b' * 115)
    $longFile = $deep + '\long-path.txt'
    try {
        [void][IO.Directory]::CreateDirectory(('\\?\' + $deep))
        [IO.File]::WriteAllText(('\\?\' + $longFile), 'long path')
        $longResult = [Diskovy.DiskScanner]::Scan([string[]]@($first), 0, 2, $token, $null)
        Check ($longFile.Length -gt 260 -and $longResult.Files[0].Path -eq $longFile -and $longResult.Skipped -eq 0) 'long paths are enumerated without truncation'
    } finally {
        [IO.File]::Delete(('\\?\' + $longFile))
        if ([IO.Directory]::Exists(('\\?\' + $deep))) { [IO.Directory]::Delete(('\\?\' + $deep)) }
        if ([IO.Directory]::Exists(('\\?\' + $first))) { [IO.Directory]::Delete(('\\?\' + $first)) }
    }

    if ($ExerciseRecycle) {
        $name = 'diskovy-recycle-test-' + [guid]::NewGuid().ToString('N') + '.tmp'
        $path = Join-Path $data $name
        [IO.File]::WriteAllText($path, 'Created solely for the Recycle Bin integration test.')
        $entry = [Diskovy.DiskScanner]::Scan([string[]]@($data), 0, 20, $token, $null).Files | Where-Object Name -eq $name
        Check $entry.CanRecycle 'temporary file on eligible drive'
        [Diskovy.FileActions]::Recycle($entry)
        Check (-not [IO.File]::Exists($path)) 'recycle removes the selected fixture from its folder'
        $shell = New-Object -ComObject Shell.Application
        $bin = $shell.NameSpace(10)
        $found = @($bin.Items() | Where-Object { [IO.Path]::GetFileNameWithoutExtension($_.Name) -eq [IO.Path]::GetFileNameWithoutExtension($name) })
        Check ($found.Count -eq 1) 'selected fixture is present in the Windows Recycle Bin'
    }
} finally {
    if ([IO.Directory]::Exists($junction)) { [IO.Directory]::Delete($junction) }
    $target = [IO.Path]::GetFullPath($fixture)
    if ([IO.Directory]::Exists($target) -and [IO.Path]::GetDirectoryName($target) -eq $parent -and [IO.Path]::GetFileName($target) -match '^diskovy-test-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}
