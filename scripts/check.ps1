param([switch]$Ui)
$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try{
 & dotnet run --project tests/CoreChecks/AxisLab.CoreChecks.csproj -c Release --no-restore -m:1 -nr:false
 if($LASTEXITCODE -ne 0){throw 'Core checks failed'}
 & dotnet run --project tests/CommunicationChecks/AxisLab.CommunicationChecks.csproj -c Release --no-restore -m:1 -nr:false
 if($LASTEXITCODE -ne 0){throw 'Communication checks failed'}
 if($Ui){
  & node scripts/web-checks.mjs
  if($LASTEXITCODE -ne 0){throw 'Web checks failed'}
  & node scripts/wpf-direct-checks.mjs
  if($LASTEXITCODE -ne 0){throw 'WPF checks failed'}
 }
}finally{Pop-Location}
