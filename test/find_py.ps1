param([string]$Pattern)
Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
  Where-Object { $_.CommandLine -like "*$Pattern*" } |
  Select-Object -ExpandProperty ProcessId
