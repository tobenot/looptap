Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
  Where-Object { $_.CommandLine -like '*tone_player*' } |
  Select-Object -ExpandProperty ProcessId
