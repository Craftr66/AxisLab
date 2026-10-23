param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
& dotnet build (Join-Path $PSScriptRoot 'AxisLab.WpfDemo.csproj') -c $Configuration -m:1 -nr:false
if($LASTEXITCODE -ne 0){throw 'WPF build failed'}
Write-Host 'For the complete two-application package, run scripts/publish.ps1 at the repository root.'
