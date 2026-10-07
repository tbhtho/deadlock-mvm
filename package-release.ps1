param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$output = Join-Path $root "release/portable-$stamp"
$payload = Join-Path $output 'DeadlockMVM'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
& dotnet publish (Join-Path $root 'src/DeadlockMVM.Launcher/DeadlockMVM.Launcher.csproj') `
    -c Release -p:PublishProfile=Portable -p:DebugType=None -p:DebugSymbols=false -o $payload
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
$manifest = Get-Content (Join-Path $root 'src/DeadlockMVM.Launcher/obj/Release/net8.0-windows/win-x64/portable-bundle-files.txt')
foreach ($runtimeFile in @('System.Private.CoreLib.dll', 'PresentationFramework.dll', 'wpfgfx_cor3.dll')) {
    if ($runtimeFile -notin $manifest) { throw "Bundled runtime missing: $runtimeFile" }
}
if (@($manifest | Where-Object { $_ -match '(^|[/\\])(System.Windows.Forms[^/\\]*|WindowsFormsIntegration)\.dll$|\.pdb$' }).Count) {
    throw 'Bundle contains excluded Windows Forms assemblies or debug symbols.'
}

# Keep the redistribution terms for the exact runtime packs used by restore.
$assets = Get-Content (Join-Path $root 'src/DeadlockMVM.Launcher/obj/project.assets.json') -Raw | ConvertFrom-Json
$packages = @($assets.packageFolders.PSObject.Properties.Name)[0]
foreach ($pack in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
    $entry = @($assets.project.frameworks.PSObject.Properties.Value.downloadDependencies | Where-Object name -eq $pack)[0]
    if (-not $entry) { throw "Runtime pack metadata missing: $pack" }
    $version = $entry.version.Trim('[', ']').Split(',')[0].Trim()
    $packRoot = Join-Path $packages ($pack.ToLowerInvariant() + '/' + $version)
    $notices = @(Get-ChildItem -LiteralPath $packRoot -File | Where-Object Name -Match '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$')
    if (-not $notices.Count) { throw "Runtime license missing: $pack" }
    foreach ($notice in $notices) {
        Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $payload "THIRD_PARTY_LICENSES/$pack-$($notice.BaseName).txt")
    }
}
$required = @('DeadlockMVM.Launcher.exe', 'DeadlockMVM.Native.dll', 'LICENSE', 'THIRD_PARTY_NOTICES.md',
    'THIRD_PARTY_LICENSES/DearImGui-LICENSE.txt', 'THIRD_PARTY_LICENSES/libgmavi-LICENSE.txt')
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)) { throw "Missing $name" }
}
$unexpected = @(Get-ChildItem -LiteralPath $payload -File | Where-Object Name -NotIn $required)
if ($unexpected.Count) { throw "Unexpected runtime files: $($unexpected.Name -join ', ')" }
foreach ($binary in @('DeadlockMVM.Launcher.exe', 'DeadlockMVM.Native.dll')) {
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload $binary)).FileVersion
    if ($version -ne '1.0.0.0') { throw "Unexpected V1 metadata: $binary $version" }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $output 'DeadlockMVM-Windows-x64.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Package: $zip"
Write-Output "ZIP bytes: $((Get-Item -LiteralPath $zip).Length)"
