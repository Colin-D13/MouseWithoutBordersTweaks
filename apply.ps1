<#
  Patches PowerToys Mouse Without Borders (mwb.patch):
  - 4k/8k polling mice don't flood the remote PC: mouse moves are coalesced (MaxMouseMoveHz).
  - Monitor 1 (primary) is the main screen: its free edges switch machines and the cursor returns onto it.
  - Edge resistance: the cursor has to hit a screen edge fast enough to switch PCs (EdgeSwitchSpeed).
  Settings live in settings.ini next to this script and apply without a restart.
  PowerToys updates overwrite the patch; MwbAutoPatch.exe re-runs this automatically.

    .\apply.ps1          rebuild MWB for the installed PowerToys version and install it
    .\apply.ps1 -Revert  put the original DLL back (also stops MwbAutoPatch.exe re-applying it)
#>
param([switch]$Revert)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$disabledMarker = Join-Path $root 'disabled'
$pt = Join-Path $env:LOCALAPPDATA 'PowerToys'
$dll = Join-Path $pt 'PowerToys.MouseWithoutBorders.dll'
$ver = (Get-Item $dll).VersionInfo.FileVersion
$backup = "$dll.orig-$ver"

function Restart-PowerToys([scriptblock]$WhileStopped) {
    Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($pt, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 50; $i++) {
        try { [IO.File]::Open($dll, 'Open', 'ReadWrite', 'None').Dispose(); break } catch { Start-Sleep -Milliseconds 200 }
    }
    try {
        & $WhileStopped
    }
    finally {
        # Run from MwbAutoPatch.exe or your own terminal. Run from inside an app container (e.g. an AI assistant's shell),
        # PowerToys would start inside that container too.
        Start-Process (Join-Path $pt 'PowerToys.exe')
    }
}

if ($Revert) {
    if (-not (Test-Path $backup)) { throw "No original DLL backed up for $ver ($backup)." }
    Restart-PowerToys { Copy-Item $backup $dll -Force }
    Set-Content $disabledMarker 'Created by apply.ps1 -Revert; run apply.ps1 to re-enable.'
    "Restored original Mouse Without Borders $ver."
    return
}

if (Test-Path $disabledMarker) { Remove-Item $disabledMarker }
if (-not (Test-Path $backup)) { Copy-Item $dll $backup }

$work = Join-Path $root "build\$ver"
$src = Join-Path $work 'src'
$app = Join-Path $work 'App'
$res = Join-Path $work 'res'
$out = Join-Path $work 'out'

# .NET SDK matching the runtime PowerToys ships with.
$tfm = (Get-Content (Join-Path $pt 'PowerToys.MouseWithoutBorders.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions.tfm
$dotnet = Join-Path $root 'dotnet\dotnet.exe'
$channel = $tfm.Substring(3)
if (-not ((Test-Path $dotnet) -and ((& $dotnet --list-sdks) -match "^$([regex]::Escape($channel.Split('.')[0]))\."))) {
    $installer = Join-Path $root 'dotnet-install.ps1'
    Invoke-WebRequest -UseBasicParsing https://dot.net/v1/dotnet-install.ps1 -OutFile $installer
    & $installer -Channel $channel -InstallDir (Join-Path $root 'dotnet') -NoPath
}

# MWB source at the installed version's tag.
if (-not (Test-Path (Join-Path $src 'src\modules\MouseWithoutBorders\App'))) {
    if (Test-Path $src) { Remove-Item -Recurse -Force $src }
    $tag = $null
    foreach ($t in @("v$ver", ('v' + ($ver -replace '\.0$', '')))) {
        if (git ls-remote --tags https://github.com/microsoft/PowerToys.git "refs/tags/$t") { $tag = $t; break }
    }
    if (-not $tag) { throw "No PowerToys source tag found for $ver." }
    git clone --quiet --depth 1 --filter=blob:none --sparse --branch $tag https://github.com/microsoft/PowerToys.git $src
    if ($LASTEXITCODE -ne 0) { throw 'git clone failed.' }
    git -C $src sparse-checkout set src/modules/MouseWithoutBorders/App
    if ($LASTEXITCODE -ne 0) { throw 'git sparse-checkout failed.' }
}

# Apply mwb.patch (8k coalescing + monitor 1 edges) to a clean source tree, then copy it out.
git -C $src reset --quiet --hard
git -C $src clean -fdq
git -C $src apply -3 --whitespace=nowarn (Join-Path $root 'mwb.patch')
if ($LASTEXITCODE -ne 0) { throw 'mwb.patch no longer applies to this PowerToys version; nothing was installed.' }
foreach ($d in @($app, $res, $out, (Join-Path $work 'gen'))) { if (Test-Path $d) { Remove-Item -Recurse -Force $d } }
Copy-Item -Recurse (Join-Path $src 'src\modules\MouseWithoutBorders\App') $app
Copy-Item (Join-Path $root 'MouseWithoutBorders.csproj') $app -Force

# Reuse the original DLL's compiled resources as-is.
New-Item -ItemType Directory -Force $res | Out-Null
$asm = [Reflection.Assembly]::ReflectionOnlyLoadFrom($backup)
foreach ($n in $asm.GetManifestResourceNames()) {
    $in = $asm.GetManifestResourceStream($n)
    $file = [IO.File]::Create((Join-Path $res $n))
    $in.CopyTo($file)
    $file.Dispose()
    $in.Dispose()
}

# Build settings that must match the installed build.
$commonProps = [IO.File]::ReadAllText((Join-Path $src 'src\Common.Dotnet.props'))
$packagesProps = [IO.File]::ReadAllText((Join-Path $src 'Directory.Packages.props'))
$tfmSuffix = [regex]::Match($commonProps, '<TargetFramework>\$\(CoreTargetFramework\)(-windows[\d.]+)<').Groups[1].Value
$winSdk = [regex]::Match($commonProps, '<WindowsSdkPackageVersion>([^<]+)<').Groups[1].Value
$csWinRT = [regex]::Match($packagesProps, 'Include="Microsoft\.Windows\.CsWinRT" Version="([^"]+)"').Groups[1].Value

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
& $dotnet build (Join-Path $app 'MouseWithoutBorders.csproj') -c Release -nologo -v:minimal -o $out `
    "-p:PTDir=$pt" "-p:MwbTfm=$tfm$tfmSuffix" "-p:MwbVersion=$ver" "-p:MwbWinSdk=$winSdk" "-p:MwbCsWinRT=$csWinRT" `
    "-p:MwbGenDir=$work\gen" "-p:MwbResDir=$res" "-p:CsWinRTWindowsMetadata=$env:windir\System32\WinMetadata"
if ($LASTEXITCODE -ne 0) { throw 'Build failed; nothing was installed.' }
& $dotnet build-server shutdown | Out-Null

$built = Join-Path $out 'PowerToys.MouseWithoutBorders.dll'
Restart-PowerToys { Copy-Item $built $dll -Force }
"Installed patched Mouse Without Borders $ver (original saved as $backup)."
