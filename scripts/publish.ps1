param([switch]$SkipBuild,[switch]$Refresh)
$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try{
 if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1')}
 $version=([xml](Get-Content -LiteralPath src/Desktop/AxisLab.Desktop.csproj -Raw)).Project.PropertyGroup.Version
 $output=Join-Path $PWD "dist\AxisLab-$version-win-x64"
 if((Test-Path -LiteralPath $output)-and -not $Refresh){throw "Output exists: $output"}
 if($Refresh -and (Test-Path -LiteralPath $output)){
  $old=Join-Path $output 'manifest.json'
  if(-not(Test-Path -LiteralPath $old)-or (Get-Content -LiteralPath $old -Raw|ConvertFrom-Json).version -ne $version){throw 'Refresh requires a matching manifest'}
 }
 New-Item -ItemType Directory -Path $output -Force|Out-Null
 foreach($project in @('src/Desktop/AxisLab.Desktop.csproj','src/VirtualCamera/AxisLab.VirtualCamera.csproj')){
  & dotnet publish $project -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.12 -p:NuGetAudit=false -p:DebugType=None -m:1 -nr:false --packages .cache/nuget-packages -o $output
  if($LASTEXITCODE -ne 0){throw "Publish failed: $project"}
 }
 Copy-Item -LiteralPath README.md,Start-AxisLab.ps1,Start-AxisLab.cmd -Destination $output -Force
 New-Item -ItemType Directory -Path (Join-Path $output 'Docs'),(Join-Path $output 'Samples') -Force|Out-Null
 Get-ChildItem -LiteralPath docs -File | Copy-Item -Destination (Join-Path $output 'Docs') -Force
 $sampleRoot=(Resolve-Path -LiteralPath samples).Path
 foreach($file in Get-ChildItem -LiteralPath $sampleRoot -Recurse -File){
  $relative=$file.FullName.Substring($sampleRoot.Length+1)
  if($relative -match '(^|[\\/])(bin|obj|VmIntegration)([\\/]|$)'){continue}
  $target=Join-Path (Join-Path $output 'Samples') $relative;New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force|Out-Null
  Copy-Item -LiteralPath $file.FullName -Destination $target -Force
 }
 $licenses=Join-Path $output 'Licenses';New-Item -ItemType Directory -Path $licenses -Force|Out-Null
 Copy-Item -LiteralPath node_modules/three/LICENSE -Destination (Join-Path $licenses 'Three.js-MIT.txt') -Force
 foreach($runtime in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64','microsoft.aspnetcore.app.runtime.win-x64')){
  Get-ChildItem -LiteralPath (Join-Path '.cache/nuget-packages' "$runtime/10.0.12") -File|Where-Object {$_.Name -match 'LICENSE|NOTICE'}|ForEach-Object{Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licenses "$runtime-$($_.Name)") -Force}
 }
 Get-ChildItem -LiteralPath .cache/nuget-packages/microsoft.web.webview2/1.0.4258.31 -File|Where-Object {$_.Name -match 'LICENSE|NOTICE'}|Copy-Item -Destination $licenses -Force
 $manifest=@{version=$version;builtUtc=[DateTimeOffset]::UtcNow.ToString('O');primary='AxisLab.exe';requiresWpf=$false;files=@()}
 foreach($file in Get-ChildItem -LiteralPath $output -Recurse -File){
  $relative=$file.FullName.Substring($output.Length+1).Replace('\','/');if($relative -eq 'manifest.json'){continue}
  if($relative -match 'VmBridge|VmSdk|blockly|AxisLab.WpfDemo.exe|(^|/)(VM.Core|VM.PlatformSDKCS)\.dll$'){throw "Unexpected package content: $relative"}
  $manifest.files+=@{path=$relative;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
 }
 [IO.File]::WriteAllText((Join-Path $output 'manifest.json'),($manifest|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
 $zip="$output.zip";Compress-Archive -LiteralPath $output -DestinationPath $zip -Force
 $sha=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
 [IO.File]::WriteAllText("$zip.sha256",$sha+'  '+[IO.Path]::GetFileName($zip)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
 Write-Host "Package: $zip";Write-Host "SHA256: $sha"
}finally{Pop-Location}
