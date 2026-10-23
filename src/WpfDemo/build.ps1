param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
& dotnet build (Join-Path $PSScriptRoot 'AxisLab.WpfDemo.csproj') -c $Configuration -m:1 -nr:false
if($LASTEXITCODE -ne 0){throw 'WPF build failed'}
 $cameraProject=Join-Path $PSScriptRoot '../VirtualCamera/AxisLab.VirtualCamera.csproj'
 if(-not(Test-Path -LiteralPath $cameraProject)){$cameraProject=Join-Path $PSScriptRoot 'Tools/VirtualCamera/AxisLab.VirtualCamera.csproj'}
 if(Test-Path -LiteralPath $cameraProject){
  & dotnet build $cameraProject -c $Configuration -m:1 -nr:false
  if($LASTEXITCODE -ne 0){throw 'Virtual camera build failed'}
  $target=Join-Path $PSScriptRoot "bin/$Configuration/net9.0-windows/VirtualCamera"
  New-Item -ItemType Directory -Path $target -Force | Out-Null
  Get-ChildItem -LiteralPath (Join-Path (Split-Path $cameraProject -Parent) "bin/$Configuration/net10.0-windows") -File | Copy-Item -Destination $target -Force
 }
Write-Host 'For the complete two-application package, run scripts/publish.ps1 at the repository root.'
