param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$cmakeCandidates = @(
    "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
)
$cmake = $cmakeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $cmake) {
    $command = Get-Command cmake -ErrorAction SilentlyContinue
    if ($command) { $cmake = $command.Source }
}
if (-not $cmake) {
    throw "CMake was not found. Install Visual Studio 2022 Desktop development with C++."
}

$buildDirectory = Join-Path $PSScriptRoot "build"
& $cmake -S $PSScriptRoot -B $buildDirectory -G "Visual Studio 17 2022" -A x64
if ($LASTEXITCODE -ne 0) { throw "Native CMake configure failed with exit code $LASTEXITCODE." }

& $cmake --build $buildDirectory --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE." }
