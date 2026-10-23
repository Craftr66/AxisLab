param([switch]$Ui)
$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try{
 & dotnet run --project tests/CoreChecks/AxisLab.CoreChecks.csproj -c Release --no-restore -m:1 -nr:false
 if($LASTEXITCODE -ne 0){throw 'Core checks failed'}
 & dotnet run --project tests/CommunicationChecks/AxisLab.CommunicationChecks.csproj -c Release --no-restore -m:1 -nr:false
 if($LASTEXITCODE -ne 0){throw 'Communication checks failed'}
 & dotnet run --project tests/WorkcellChecks/AxisLab.WorkcellChecks.csproj -c Release --no-restore -m:1 -nr:false
 if($LASTEXITCODE -ne 0){throw 'Workcell checks failed'}
 if($Ui){
  & node scripts/web-checks.mjs
  if($LASTEXITCODE -ne 0){throw 'Web checks failed'}
  & node scripts/workcell-checks.mjs
  if($LASTEXITCODE -ne 0){throw 'Workcell integration checks failed'}
 }
}finally{Pop-Location}
