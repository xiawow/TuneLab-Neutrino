param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$package = Join-Path $root "artifacts\package"
$tlx = Join-Path $root "artifacts\TuneLab.NeutrinoV3-win-x64.tlx"

if (Test-Path -LiteralPath $package) {
    Remove-Item -LiteralPath $package -Recurse -Force
}
dotnet build (Join-Path $root "TuneLab.NeutrinoV3.csproj") -c $Configuration

# TuneLab's isolated loader uses the dependency file for native resolution.
# Keep root copies for normal Windows probing and matching runtime-path copies
# for AssemblyDependencyResolver.
$native = Join-Path $package "runtimes\win-x64\native"
New-Item -ItemType Directory -Path $native -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $package "onnxruntime.dll") -Destination $native
Copy-Item -LiteralPath (Join-Path $package "onnxruntime_providers_shared.dll") -Destination $native
Remove-Item -LiteralPath (Join-Path $package "onnxruntime.lib") -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $package "onnxruntime_providers_shared.lib") -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $package "TuneLab.NeutrinoV3.pdb") -Force -ErrorAction SilentlyContinue

if (Test-Path -LiteralPath $tlx) {
    Remove-Item -LiteralPath $tlx -Force
}
$zip = [System.IO.Path]::ChangeExtension($tlx, ".zip")
if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}
Compress-Archive -Path (Join-Path $package "*") -DestinationPath $zip -CompressionLevel Optimal
Move-Item -LiteralPath $zip -Destination $tlx
Write-Host "Created $tlx"
