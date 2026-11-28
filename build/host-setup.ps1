# PowerShell 7 on Windows. Reuse the two ZIPs produced by host-package.ps1.
param(
    [Parameter(Mandatory)] [string] $Packages,
    [Parameter(Mandatory)] [string] $Output,
    [string] $Compiler = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'Inno Setup compilation requires Windows.' }
$Compiler = (Get-Command $Compiler -ErrorAction Stop).Source
$version = ([xml](Get-Content -Raw (Join-Path $PSScriptRoot '../Directory.Build.props'))).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(-[a-zA-Z0-9.-]+)?$') { throw 'Invalid package version.' }
$outputDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
$prefix = "HCSL-Host-$version-win-x64"
$temp = (New-Item -ItemType Directory (Join-Path ([IO.Path]::GetTempPath()) "hcsl-setup-$([guid]::NewGuid())")).FullName
try {
    function Expand-Payload([string] $Mode) {
        $name = "$prefix-$Mode"
        $zip = (Get-Item -LiteralPath (Join-Path $Packages "$name.zip")).FullName
        $expected = ((Get-Content -LiteralPath "$zip.sha256" -Raw).Trim() -split '\s+')[0]
        if ((Get-FileHash -LiteralPath $zip).Hash -ne $expected) { throw "Checksum mismatch: $zip" }
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $temp)
        $dir = Join-Path $temp $name
        foreach ($file in @('HCSL.Host.exe', 'HCSL.Host.runtimeconfig.json', 'daemon/HCSL.Daemon.exe',
            'daemon/HCSL.Daemon.dll', 'daemon/HCSL.Daemon.runtimeconfig.json', 'LICENSE')) {
            if (!(Test-Path -LiteralPath (Join-Path $dir $file) -PathType Leaf)) { throw "Missing payload file: $name/$file" }
        }
        if (Get-ChildItem -LiteralPath $dir -Recurse -File -Force | Where-Object Extension -in '.pdb', '.dbg') {
            throw "Unexpected debug symbols in $name"
        }
        foreach ($file in @('coreclr.dll', 'daemon/coreclr.dll')) {
            if ((Test-Path -LiteralPath (Join-Path $dir $file) -PathType Leaf) -ne ($Mode -eq 'self-contained')) {
                throw "Unexpected .NET runtime layout in $name"
            }
        }
        return $dir
    }
    $bundled = Expand-Payload 'self-contained'
    $system = Expand-Payload 'framework-dependent'
    $cleanup = Join-Path $temp 'cleanup.iss'
    $checks = Join-Path $temp 'runtime-checks.iss'
    # Delete only paths from the opposite payload, never directories or wildcard matches.
    function Get-Cleanup([string] $Old, [string] $New, [string] $Component) {
        foreach ($file in Get-ChildItem -LiteralPath $Old -File -Recurse -Force) {
            $relative = [IO.Path]::GetRelativePath($Old, $file.FullName)
            if ($relative -match '["{}*?]') { throw "Unsupported installer path: $relative" }
            if (!(Test-Path -LiteralPath (Join-Path $New $relative))) {
                'Type: files; Name: "{{app}}\{0}"; Components: {1}; Check: IsModeSwitch' -f $relative, $Component
            }
        }
    }
    @((Get-Cleanup $bundled $system 'system'); (Get-Cleanup $system $bundled 'bundled')) |
        Set-Content -LiteralPath $cleanup -Encoding utf8
    $runtimeChecks = foreach ($file in @('HCSL.Host.runtimeconfig.json', 'daemon/HCSL.Daemon.runtimeconfig.json')) {
        $config = (Get-Content -LiteralPath (Join-Path $system $file) -Raw | ConvertFrom-Json).runtimeOptions
        $frameworks = @(@($config.framework) + @($config.frameworks) | Where-Object { $_ })
        if (!$frameworks -or ($config.rollForward -and $config.rollForward -notin 'Minor', 'LatestPatch')) {
            throw "Unsupported runtime configuration: $file"
        }
        foreach ($framework in $frameworks) {
            if ($framework.name -notmatch '^Microsoft\.[A-Za-z.]+$' -or $framework.version -notmatch '^10\.0\.\d+$') {
                throw "Unexpected framework requirement in $file"
            }
            "    if not HasRuntime('$($framework.name)', '$($framework.version)') then begin"
            "      Result := 'Install x64 $($framework.name) $($framework.version) or a newer .NET 10 patch, or select the bundled runtime.';"
            '      Exit;'
            '    end;'
        }
    }
    $runtimeChecks | Set-Content -LiteralPath $checks -Encoding utf8
    [void][IO.Directory]::CreateDirectory($outputDir)
    & $Compiler "/DAppVersion=$version" "/DBundledDir=$bundled" "/DSystemDir=$system" "/DOutputDir=$outputDir" `
        "/DCleanupFile=$cleanup" "/DRuntimeChecksFile=$checks" (Join-Path $PSScriptRoot 'host-setup.iss')
    if ($LASTEXITCODE -ne 0) { throw "ISCC exited with code $LASTEXITCODE" }
} finally {
    Remove-Item -LiteralPath $temp -Force -Recurse
}
