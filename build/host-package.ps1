# PowerShell 7. Publish one Host GUI package for the current x64 OS.
# Run -Smoke only in a disposable user profile: Host and Daemon use normal data paths.
param(
    [Parameter(Mandatory)] [ValidateSet('self-contained', 'framework-dependent', 'aot')] [string] $Mode,
    [Parameter(Mandatory)] [string] $Output,
    [switch] $Smoke,
    [switch] $NativeDaemon
)

$ErrorActionPreference = 'Stop'
function Invoke-Checked([string] $Command, [string[]] $Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command exited with code $LASTEXITCODE" }
}

if ((!$IsWindows -and !$IsLinux) -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'Packaging requires a Windows or Linux x64 host.'
}
$root = Split-Path -Parent $PSScriptRoot
$rid = if ($IsWindows) { 'win-x64' } else { 'linux-x64' }
$version = ([xml](Get-Content -Raw (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(-[a-zA-Z0-9.-]+)?$') { throw 'Invalid package version.' }
if ($NativeDaemon -and $Mode -ne 'aot') { throw 'The native Daemon experiment requires -Mode aot.' }
$name = "HCSL-Host-$version-$rid-$Mode"
if ($NativeDaemon) { $name += '-native-daemon-experimental' }
$extension = if ($IsWindows) { 'zip' } else { 'tar.gz' }
$outputDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
$destination = Join-Path $outputDir "$name.$extension"
$symbolsName = "$name-symbols"
$symbolsDestination = Join-Path $outputDir "$symbolsName.$extension"
foreach ($path in @($destination, $symbolsDestination)) {
    if (Test-Path -LiteralPath $path) { throw "Package already exists: $path" }
}
$temp = New-Item -ItemType Directory (Join-Path ([IO.Path]::GetTempPath()) "hcsl-$([guid]::NewGuid())")
try {
    $stage = Join-Path $temp.FullName $name
    $contained = ($Mode -ne 'framework-dependent').ToString().ToLowerInvariant()
    $aot = ($Mode -eq 'aot').ToString().ToLowerInvariant()
    $daemonAot = $NativeDaemon.IsPresent.ToString().ToLowerInvariant()
    Invoke-Checked dotnet @('publish', (Join-Path $root 'src/HCSL.Host/HCSL.Host.csproj'), '-c', 'Release', '-r', $rid,
        '--self-contained', $contained, '-p:PublishSingleFile=false', "-p:HostPublishAot=$aot", "-p:BundledDaemonAot=$daemonAot", '-o', $stage)
    $exe = if ($IsWindows) { '.exe' } else { '' }
    $identityFile = if ($NativeDaemon) { 'daemon/daemon.native-aot' } else { 'daemon/HCSL.Daemon.dll' }
    foreach ($file in @("HCSL.Host$exe", "daemon/HCSL.Daemon$exe", $identityFile)) {
        if (!(Test-Path -LiteralPath (Join-Path $stage $file) -PathType Leaf)) { throw "Missing published file: $file" }
    }
    $runtimeFile = if ($IsWindows) { 'coreclr.dll' } else { 'libcoreclr.so' }
    $managedHost = Test-Path -LiteralPath (Join-Path $stage 'HCSL.Host.runtimeconfig.json')
    $hostRuntime = Test-Path -LiteralPath (Join-Path $stage $runtimeFile)
    $daemonRuntime = Test-Path -LiteralPath (Join-Path $stage "daemon/$runtimeFile")
    if ($managedHost -ne ($Mode -ne 'aot') -or $hostRuntime -ne ($Mode -eq 'self-contained') -or
        $daemonRuntime -ne (!$NativeDaemon -and $Mode -ne 'framework-dependent')) {
        throw "Published layout does not match $Mode."
    }
    $symbolsStage = Join-Path $temp.FullName $symbolsName
    foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse -Force) {
        if ($file.Extension -notin '.pdb', '.dbg') { continue }
        $relative = [IO.Path]::GetRelativePath($stage, $file.FullName)
        $target = Join-Path $symbolsStage $relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        Move-Item -LiteralPath $file.FullName -Destination $target
    }
    $symbolsArchive = Join-Path $temp.FullName "$symbolsName.$extension"
    if (Test-Path -LiteralPath $symbolsStage) {
        if ($IsWindows) {
            [IO.Compression.ZipFile]::CreateFromDirectory($symbolsStage, $symbolsArchive, [IO.Compression.CompressionLevel]::Optimal, $true)
        } else {
            Invoke-Checked tar @('-czf', $symbolsArchive, '-C', $temp.FullName, $symbolsName)
        }
    }
    Copy-Item (Join-Path $root 'LICENSE') $stage
    $archive = Join-Path $temp.FullName "$name.$extension"
    if ($IsWindows) {
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
    } else {
        Invoke-Checked tar @('-czf', $archive, '-C', $temp.FullName, $name)
    }
    if ($Smoke) {
        $unpack = (New-Item -ItemType Directory (Join-Path $temp.FullName 'unpacked')).FullName
        if ($IsWindows) { [IO.Compression.ZipFile]::ExtractToDirectory($archive, $unpack) }
        else { Invoke-Checked tar @('-xzf', $archive, '-C', $unpack) }
        $launcher = Join-Path (Join-Path $unpack $name) "HCSL.Host$exe"
        if ($IsWindows) {
            $process = Start-Process -FilePath $launcher -ArgumentList '--package-smoke' -PassThru
            if (!$process.WaitForExit(45000)) { $process.Kill(); throw 'Host smoke timed out.' }
            if ($process.ExitCode -ne 0) { throw "Host smoke exited with code $($process.ExitCode)." }
        } else {
            Invoke-Checked timeout @('45s', 'xvfb-run', '-a', $launcher, '--package-smoke')
        }
    }
    New-Item -ItemType Directory -Force $outputDir | Out-Null
    Move-Item -LiteralPath $archive -Destination $destination
    if (Test-Path -LiteralPath $symbolsArchive -PathType Leaf) {
        Move-Item -LiteralPath $symbolsArchive -Destination $symbolsDestination
    }
    $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name.$extension" | Set-Content -Encoding ascii "$destination.sha256"
} finally {
    # Only stop the daemon launched from this invocation's private extraction directory.
    $privatePath = $temp.FullName + [IO.Path]::DirectorySeparatorChar
    Get-Process -Name HCSL.Daemon -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($privatePath, [StringComparison]::OrdinalIgnoreCase)
    } | ForEach-Object { $_.Kill(); [void]$_.WaitForExit(5000) }
    Remove-Item -LiteralPath $temp.FullName -Recurse -Force
}
