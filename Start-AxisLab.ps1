$ErrorActionPreference='Stop'
if(-not $env:AXISLAB_DESKTOP_HOME){$env:AXISLAB_DESKTOP_HOME=Join-Path $env:LOCALAPPDATA 'AxisLabSuite\Simulator-2.1'}
if(-not $env:AXISLAB_MODBUS_PORT){$env:AXISLAB_MODBUS_PORT='0'}
$executable=Join-Path $PSScriptRoot 'AxisLab.exe'
if(-not(Test-Path -LiteralPath $executable)){throw 'Extract the complete AxisLab release package first.'}
Start-Process -FilePath $executable
