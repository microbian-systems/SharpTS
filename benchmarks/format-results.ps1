param(
    [string]$ResultsFile = (Join-Path ([System.IO.Path]::GetTempPath()) 'bench-results/results.txt'),
    [string]$DotNetVersion,
    [string]$NodeVersion
)

if (-not $DotNetVersion) {
    $DotNetVersion = try { dotnet --version } catch { 'unknown' }
}
if (-not $NodeVersion) {
    $NodeVersion = try { node -v } catch { 'unknown' }
}

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ResultsFile)) {
    Write-Error "Results file not found: $ResultsFile"
    exit 1
}

$os = if ($IsLinux) { 'Linux' } elseif ($IsMacOS) { 'macOS' } else { 'Windows' }
$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture

Write-Output @"
## SharpTS Cross-Runtime Benchmark Results

**Environment:** .NET $DotNetVersion | Node.js $NodeVersion | $os $arch
**Date:** $(Get-Date -Format 'yyyy-MM-dd' -AsUTC)

| Benchmark | Param | Interpreter (ms) | Compiled (ms) | Node.js (ms) | Compiled vs Node |
|-----------|-------|------------------:|--------------:|--------------:|-----------------:|
"@

# Parse results into a dictionary keyed by "bench|param"
$data = [ordered]@{}
foreach ($line in Get-Content $ResultsFile) {
    if (-not $line.Trim()) { continue }
    $parts = $line -split '\|', 2
    $runtime = $parts[0]
    $fields = $parts[1] -split ':'
    $bench = $fields[0]
    $param = $fields[1]
    $ms = $fields[2]
    $key = "$bench|$param"

    if (-not $data.Contains($key)) {
        $data[$key] = @{}
    }
    $data[$key][$runtime] = $ms
}

foreach ($key in $data.Keys) {
    $kp = $key -split '\|'
    $bench = $kp[0]
    $param = $kp[1]
    $entry = $data[$key]

    $interp = if ($entry.ContainsKey('interpreter')) { $entry['interpreter'] } else { '-' }
    $comp   = if ($entry.ContainsKey('compiled'))    { $entry['compiled'] }    else { '-' }
    $njs    = if ($entry.ContainsKey('node'))         { $entry['node'] }        else { '-' }

    $ratio = '-'
    if ($comp -ne '-' -and $njs -ne '-') {
        $njsNum = [double]$njs
        if ($njsNum -gt 0) {
            $ratio = '{0:F2}x' -f ([double]$comp / $njsNum)
        }
    }

    Write-Output "| $bench | $param | $interp | $comp | $njs | $ratio |"
}
