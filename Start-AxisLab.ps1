$ErrorActionPreference='Stop'
$env:AXISLAB_DESKTOP_HOME=Join-Path $env:LOCALAPPDATA 'AxisLabSuite\Simulator'
$env:AXISLAB_WPF_DATA=Join-Path $env:LOCALAPPDATA 'AxisLabSuite\Controller'
$env:AXISLAB_MODBUS_PORT='0'
$simulator=Join-Path $PSScriptRoot 'Simulator\AxisLab.exe'
$controller=Join-Path $PSScriptRoot 'Controller\AxisLab.WpfDemo.exe'
if(-not (Test-Path -LiteralPath $simulator) -or -not (Test-Path -LiteralPath $controller)){throw 'Use this launcher from the extracted release package.'}
$ready=$null
$process=Start-Process -FilePath $simulator -PassThru
$log=Join-Path $env:AXISLAB_DESKTOP_HOME 'desktop.log'
for($attempt=0;$attempt -lt 100;$attempt++){
 if(Test-Path -LiteralPath $log){
  $ready=Get-Content -LiteralPath $log -Tail 30 | Select-String 'DesktopReady http://'
  if($ready){
   $match=[regex]::Match($ready[-1].Line,'http://127\.0\.0\.1:\d+')
   try {$health=Invoke-RestMethod -Uri ($match.Value+'/api/health') -TimeoutSec 1;if($health.version -eq '2.0.0'){break}}catch{}
   $ready=$null
  }
 }
 if($process.HasExited -and $process.ExitCode -ne 0){throw 'Simulator did not start. Check desktop.log.'}
 Start-Sleep -Milliseconds 200
}
if(-not $ready){throw 'Simulator startup timed out. Check WebView2 Runtime and desktop.log.'}
Start-Process -FilePath $controller
