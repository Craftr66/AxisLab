param([switch]$SkipBuild,[switch]$Refresh)
$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try{
 if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1') -WithVisionMaster}
 $version='2.0.0'
 $publishRoot=Join-Path $PWD "dist\AxisLab-$version-win-x64"
 if((Test-Path -LiteralPath $publishRoot)-and -not $Refresh){throw "Output already exists. Choose a clean output before publishing: $publishRoot"}
 if($Refresh -and (Test-Path -LiteralPath $publishRoot)){
  $oldManifest=Join-Path $publishRoot 'manifest.json'
  if(-not (Test-Path -LiteralPath $oldManifest)){throw 'Refresh requires an existing package manifest'}
  if((Get-Content -LiteralPath $oldManifest -Raw|ConvertFrom-Json).version -ne $version){throw 'Refresh version mismatch'}
 }
 New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
 $projects=@(
  @{Project='src/Desktop/AxisLab.Desktop.csproj';Target='Simulator';Runtime='10.0.12'},
  @{Project='src/WpfDemo/AxisLab.WpfDemo.csproj';Target='Controller';Runtime='9.0.20'},
  @{Project='src/VirtualCamera/AxisLab.VirtualCamera.csproj';Target='Controller/VirtualCamera';Runtime='10.0.12'}
 )
 foreach($entry in $projects){
  & dotnet publish $entry.Project -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=$($entry.Runtime) -p:NuGetAudit=false -p:DebugType=None -m:1 -nr:false --packages .cache/nuget-packages -o (Join-Path $publishRoot $entry.Target)
  if($LASTEXITCODE -ne 0){throw "Publish failed: $($entry.Project)"}
 }
 & dotnet build src/VmDirectLink/AxisLab.VmDirectLink.csproj -c Release -m:1 -nr:false --packages .cache/nuget-packages
 if($LASTEXITCODE -ne 0){throw 'VM publisher build failed'}
 $integration=Join-Path $publishRoot 'VmIntegration';New-Item -ItemType Directory -Path $integration -Force | Out-Null
 Copy-Item -LiteralPath src/VmDirectLink/bin/Release/net48/AxisLab.VmDirectLink.dll -Destination $integration
 Copy-Item -LiteralPath samples/VmIntegration/UserGlobalScript.cs -Destination $integration
 Copy-Item -LiteralPath README.md,Start-AxisLab.ps1,Start-AxisLab.cmd -Destination $publishRoot
 New-Item -ItemType Directory -Path (Join-Path $publishRoot 'Docs'),(Join-Path $publishRoot 'Samples') -Force | Out-Null
 Get-ChildItem -LiteralPath docs | Copy-Item -Destination (Join-Path $publishRoot 'Docs') -Recurse -Force
 Get-ChildItem -LiteralPath samples | Copy-Item -Destination (Join-Path $publishRoot 'Samples') -Recurse -Force
 Get-ChildItem -LiteralPath (Join-Path $publishRoot 'Samples') -Directory -Recurse | Where-Object {$_.Name -in @('bin','obj')} | Sort-Object {$_.FullName.Length} -Descending | ForEach-Object {
  $resolved=[IO.Path]::GetFullPath($_.FullName)
  if(-not $resolved.StartsWith($publishRoot+[IO.Path]::DirectorySeparatorChar)){throw 'Unsafe cleanup target'}
  Remove-Item -LiteralPath $resolved -Recurse -Force
 }
 $licenses=Join-Path $publishRoot 'Licenses';New-Item -ItemType Directory -Path $licenses -Force | Out-Null
 Copy-Item -LiteralPath node_modules/three/LICENSE -Destination (Join-Path $licenses 'Three.js-MIT.txt')
 foreach($runtime in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64','microsoft.aspnetcore.app.runtime.win-x64')){
  foreach($runtimeVersion in @('9.0.20','10.0.12')){
   $runtimeRoot=Join-Path '.cache/nuget-packages' "$runtime/$runtimeVersion"
   if(Test-Path -LiteralPath $runtimeRoot){
    Get-ChildItem -LiteralPath $runtimeRoot -File | Where-Object {$_.Name -match 'LICENSE|NOTICE'} | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licenses "$runtime-$runtimeVersion-$($_.Name)")}
   }
  }
 }
 Get-ChildItem -LiteralPath .cache/nuget-packages/microsoft.web.webview2/1.0.4258.31 -File | Where-Object {$_.Name -match 'license|notice'} | Copy-Item -Destination $licenses
 $manifest=@{version=$version;builtUtc=[DateTimeOffset]::UtcNow.ToString('O');files=@()}
 foreach($file in Get-ChildItem -LiteralPath $publishRoot -Recurse -File){
  $relative=$file.FullName.Substring($publishRoot.Length+1).Replace('\','/')
  if($relative -eq 'manifest.json'){continue}
  if($relative -match 'VmBridge|VmSdk|blockly|(^|/)(VM.Core|VM.PlatformSDKCS)\.dll$'){throw "Retired or vendor binary in package: $relative"}
  $manifest.files+=@{path=$relative;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
 }
 [IO.File]::WriteAllText((Join-Path $publishRoot 'manifest.json'),($manifest|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
 $archive="$publishRoot.zip";Compress-Archive -LiteralPath $publishRoot -DestinationPath $archive -Force
 $hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
 [IO.File]::WriteAllText("$archive.sha256",$hash+'  '+[IO.Path]::GetFileName($archive)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
 Write-Host "Package: $archive"
 Write-Host "SHA256: $hash"
}finally{Pop-Location}
