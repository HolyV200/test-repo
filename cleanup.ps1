# === ULTA-CLEANER: FULL SYSTEM RESET ===
$erroractionpreference = 'SilentlyContinue'

Write-Host "[*] Starting deep clean..."

# 1. Kill any running miner/bridge processes
$procs = "dllhost_s","WerFault_r","OfficeC2R","EdgeUpdate_s","AppxSvc","RuntimeBroker_x","CLRJit","mscoree","ngenservice","mscorsvw"
Get-Process | Where-Object { $procs -contains $_.Name } | Stop-Process -Force

# 2. Remove Registry Persistence
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'WindowsUpdateCoordinator' -Force

# 3. Remove Scheduled Task
Unregister-ScheduledTask -TaskName 'MicrosoftEdgeUpdateTask' -Confirm:$false

# 4. Remove Startup Shortcut
$startupDir = [Environment]::GetFolderPath('Startup')
Remove-Item (Join-Path $startupDir 'EdgeUpdate.lnk') -Force

# 5. Remove WMI Event Subscriptions
Get-WmiObject -Namespace root\subscription -Class __EventFilter -Filter "Name='EdgeUpdateFilter'" | Remove-WmiObject
Get-WmiObject -Namespace root\subscription -Class CommandLineEventConsumer -Filter "Name='EdgeUpdateConsumer'" | Remove-WmiObject
Get-WmiObject -Namespace root\subscription -Class __FilterToConsumerBinding | Where-Object { $_.Filter -match "EdgeUpdateFilter" } | Remove-WmiObject

# 6. Wipe AppData Local Storage
$appData = "$env:LOCALAPPDATA\Microsoft\CLR\NativeImages"
if (Test-Path $appData) { Remove-Item -Path "$appData\*" -Recurse -Force }

Write-Host "[+] System cleaned. Ready for fresh deployment."
