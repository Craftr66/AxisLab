param([switch]$NoRestore,[switch]$WithVisionMaster)
$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
 & node node_modules/typescript/bin/tsc --noEmit -p src/Frontend/tsconfig.json
 if($LASTEXITCODE -ne 0){throw 'TypeScript check failed'}
 & node scripts/build.mjs
 if($LASTEXITCODE -ne 0){throw 'Frontend build failed'}
 $options=@();if($NoRestore){$options+='--no-restore'}
 & dotnet build AxisLab.slnx -c Release -m:1 -nr:false -p:NuGetAudit=false --packages .cache/nuget-packages @options
 if($LASTEXITCODE -ne 0){throw 'Solution build failed'}
 $cameraTarget=Join-Path $PWD 'src/WpfDemo/bin/Release/net9.0-windows/VirtualCamera'
 New-Item -ItemType Directory -Path $cameraTarget -Force | Out-Null
 Get-ChildItem -LiteralPath src/VirtualCamera/bin/Release/net10.0-windows -File | Copy-Item -Destination $cameraTarget -Force
 if($WithVisionMaster){
  & dotnet build src/VmDirectLink/AxisLab.VmDirectLink.csproj -c Release -m:1 -nr:false --packages .cache/nuget-packages @options
  if($LASTEXITCODE -ne 0){throw 'VM integration build failed; install VisionMaster 4.3 or pass VmAssembly to dotnet build'}
 }
}finally{Pop-Location}
