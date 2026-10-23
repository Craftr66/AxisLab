param([switch]$NoRestore,[switch]$WithVisionMaster,[switch]$IncludeWpf)
$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try{
 & node node_modules/typescript/bin/tsc --noEmit -p src/Frontend/tsconfig.json
 if($LASTEXITCODE -ne 0){throw 'TypeScript check failed'}
 & node scripts/build.mjs
 if($LASTEXITCODE -ne 0){throw 'Frontend build failed'}
 $options=@();if($NoRestore){$options+='--no-restore'}
 & dotnet build AxisLab.slnx -c Release -m:1 -nr:false -p:NuGetAudit=false --packages .cache/nuget-packages @options
 if($LASTEXITCODE -ne 0){throw 'AxisLab build failed'}
 $cameraTarget=Join-Path $PWD 'src/Desktop/bin/Release/net10.0-windows/VirtualCamera'
 New-Item -ItemType Directory -Path $cameraTarget -Force|Out-Null
 Get-ChildItem -LiteralPath src/VirtualCamera/bin/Release/net10.0-windows -File|Copy-Item -Destination $cameraTarget -Force
 if($IncludeWpf){
  & dotnet build src/WpfDemo/AxisLab.WpfDemo.csproj -c Release -m:1 -nr:false -p:NuGetAudit=false --packages .cache/nuget-packages @options
  if($LASTEXITCODE -ne 0){throw 'Optional WPF build failed'}
 }
 if($WithVisionMaster){
  & dotnet build src/VmDirectLink/AxisLab.VmDirectLink.csproj -c Release -m:1 -nr:false --packages .cache/nuget-packages @options
  if($LASTEXITCODE -ne 0){throw 'Optional VM result publisher build failed'}
 }
}finally{Pop-Location}
