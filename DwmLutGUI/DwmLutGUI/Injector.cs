using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DwmLutGUI
{
    internal static class Injector
    {
        public static readonly bool NoDebug;

        private static readonly string DllName;
        private static readonly string DllPath;
        private static readonly string LutsPath;

        /// <summary>
        /// SDR-in-HDR gamma fix. When true, a marker file is staged next to the LUTs and the
        /// injected DLL patches DWM's SDR->scRGB shaders to a pure gamma curve. After injecting
        /// (or unloading) we cycle advanced colour so DWM rebuilds those shaders.
        /// </summary>
        public static bool GammaFixEnabled { get; set; }

        /// <summary>
        /// Target gamma for the fix, chosen in the GUI. 2.2 = PC/sRGB nominal, 2.4 = BT.1886
        /// (dark room, and ledoge's own default), 2.6 = DCI. Only the exponent changes; the
        /// piecewise breakpoint/offset/scale are always flattened to 0/0/1.
        /// </summary>
        public static double GammaFixValue { get; set; } = 2.4;

        private static readonly IntPtr LoadlibraryA;
        private static readonly IntPtr FreeLibrary;

        static Injector()
        {
            var basePath = Environment.ExpandEnvironmentVariables("%SYSTEMROOT%\\Temp\\");
            DllName = "dwm_lut.dll";
            DllPath = basePath + DllName;
            LutsPath = basePath + "luts\\";

            var kernel32 = GetModuleHandle("kernel32.dll");
            LoadlibraryA = GetProcAddress(kernel32, "LoadLibraryA");
            FreeLibrary = GetProcAddress(kernel32, "FreeLibrary");

            try
            {
                Process.EnterDebugMode();
            }
            catch (Exception)
            {
#if !DEBUG
                MessageBox.Show("Failed to enter debug mode – will not be able to apply LUTs.");
#endif
                NoDebug = true;
            }
        }

        public static bool? GetStatus()
        {
            if (NoDebug) return null;

            var dwmInstances = Process.GetProcessesByName("dwm");
            if (dwmInstances.Length == 0) return null;

            bool? result = false;
            foreach (var dwm in dwmInstances)
            {
                try
                {
                    var modules = dwm.Modules;
                    foreach (ProcessModule module in modules)
                    {
                        if (module.ModuleName == DllName)
                        {
                            result = true;
                        }

                        module.Dispose();
                    }
                }
                catch
                {
                    result = null;
                }

                dwm.Dispose();
            }

            return result;
        }

        private static void CopyOrConvertLut(string source, string dest)
        {
            var extension = source.Split('.').Last().ToLower();
            switch (extension)
            {
                case "cube":
                    File.Copy(source, dest);
                    ClearPermissions(dest);
                    break;
                case "txt":
                {
                    var lines = File.ReadAllLines(source);

                    using (var file = new StreamWriter(dest))
                    {
                        file.WriteLine("LUT_3D_SIZE 65");

                        for (var b = 0; b < 65; b++)
                        {
                            for (var g = 0; g < 65; g++)
                            {
                                for (var r = 0; r < 65; r++)
                                {
                                    var line = lines[g + 65 * (r + 65 * b)];
                                    var start = 1;
                                    var found = 0;

                                    while (found != 3)
                                    {
                                        if (line[start++] == ' ') found++;
                                    }

                                    file.WriteLine(line.Substring(start));
                                }
                            }
                        }
                    }

                    ClearPermissions(dest);
                    break;
                }
                default:
                    throw new Exception("Unsupported LUT format: " + extension);
            }
        }

        private static void ElevatePrivilege()
        {
            var pid = Process.GetProcessesByName("lsass")[0].Id;
            var processHandle = OpenProcess(DesiredAccess.ProcessQueryLimitedInformation, true, (uint)pid);
            var openProcessResult = OpenProcessToken(processHandle, DesiredAccess.MaximumAllowed, out var impersonatedTokenHandle);
            if (!openProcessResult)
            {
                throw new Exception("Failed to open process token");
            }
            var impersonateResult = ImpersonateLoggedOnUser(impersonatedTokenHandle);
            if (!impersonateResult)
            {
                throw new Exception("Failed to impersonate logged on user");
            }

            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                if (!identity.IsSystem)
                {
                    throw new Exception("Not running as SYSTEM");
                }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            [Out] byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

        // The sRGB linear-segment breakpoint, stored in the shaders as a 3-float run.
        // The EOTF patch rewrites it to 0, so counting occurrences tells us whether the
        // patch is currently live in DWM.
        private static byte[] SrgbBreakpointTriple()
        {
            var one = BitConverter.GetBytes(0.04045f);
            var t = new byte[12];
            Buffer.BlockCopy(one, 0, t, 0, 4);
            Buffer.BlockCopy(one, 0, t, 4, 4);
            Buffer.BlockCopy(one, 0, t, 8, 4);
            return t;
        }

        private static int CountOccurrences(byte[] haystack, int length, byte[] needle)
        {
            var count = 0;
            for (var i = 0; i + needle.Length <= length; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) { count++; i += needle.Length - 1; }
            }
            return count;
        }

        /// <summary>
        /// Reports whether the EOTF gamma fix is currently live in DWM, by comparing DWM's
        /// mapped dwmcore.dll against the pristine copy on disk. The patch clears the sRGB
        /// breakpoint constant in 4 shaders, so a live patch shows exactly 4 fewer
        /// occurrences than the on-disk file. This is ground truth, like GetStatus() - it
        /// survives GUI restarts and reflects a DWM that was patched by any means.
        /// Returns null if it cannot be determined.
        /// </summary>
        // The on-disk occurrence count never changes for a given dwmcore.dll, so it is cached
        // rather than re-read (4+ MB) on every check.
        private static string _diskCountKey;
        private static int _diskCount;

        // The full check reads the whole mapped image out of DWM, so it is throttled: the status
        // timer gets a cached answer, and anything that actually changes the state forces a fresh
        // one. Without this the 1s UI timer was moving ~9 MB/s in and out of dwm.exe forever.
        private static readonly System.Diagnostics.Stopwatch _statusAge = new System.Diagnostics.Stopwatch();
        private static bool? _statusCache;
        private const int StatusMaxAgeMs = 10000;

        public static bool? GetGammaFixStatus(bool force = false)
        {
            if (!force && _statusAge.IsRunning && _statusAge.ElapsedMilliseconds < StatusMaxAgeMs)
            {
                return _statusCache;
            }

            var result = GetGammaFixStatusUncached();
            _statusCache = result;
            _statusAge.Restart();
            return result;
        }

        private static bool? GetGammaFixStatusUncached()
        {
            try
            {
                var dwmInstances = Process.GetProcessesByName("dwm");
                if (dwmInstances.Length == 0) return null;

                var needle = SrgbBreakpointTriple();

                foreach (var dwm in dwmInstances)
                {
                    IntPtr baseAddr = IntPtr.Zero;
                    var size = 0;
                    string modulePath = null;
                    try
                    {
                        foreach (ProcessModule m in dwm.Modules)
                        {
                            if (string.Equals(m.ModuleName, "dwmcore.dll", StringComparison.OrdinalIgnoreCase))
                            {
                                baseAddr = m.BaseAddress;
                                size = m.ModuleMemorySize;
                                modulePath = m.FileName;
                                break;
                            }
                        }
                    }
                    catch { continue; }

                    if (baseAddr == IntPtr.Zero || size <= 0 || modulePath == null) continue;

                    // MaximumAllowed covers PROCESS_VM_READ; the caller has already elevated.
                    var hProcess = OpenProcess(DesiredAccess.MaximumAllowed, false, (uint)dwm.Id);
                    if (hProcess == IntPtr.Zero) continue;

                    try
                    {
                        var buffer = new byte[size];
                        IntPtr read;
                        if (!ReadProcessMemory(hProcess, baseAddr, buffer, size, out read) ||
                            read.ToInt64() <= 0)
                            continue;

                        var live = CountOccurrences(buffer, (int)read.ToInt64(), needle);

                        // Cache the on-disk count: it is fixed for a given dwmcore.dll.
                        var fi = new FileInfo(modulePath);
                        var key = modulePath + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
                        if (_diskCountKey != key)
                        {
                            var diskBytes = File.ReadAllBytes(modulePath);
                            _diskCount = CountOccurrences(diskBytes, diskBytes.Length, needle);
                            _diskCountKey = key;
                            GuiDiag.Log("[status] cached on-disk sRGB occurrence count = " + _diskCount +
                                        " for " + modulePath);
                        }
                        var onDisk = _diskCount;

                        if (onDisk == 0)
                        {
                            GuiDiag.Log("[status] sRGB constant not found in dwmcore on disk -> undetermined");
                            return null;
                        }

                        var active = live <= onDisk - 4;
                        GuiDiag.Log("[status] sRGB breakpoint occurrences: live=" + live + " onDisk=" + onDisk +
                                    " -> gamma fix " + (active ? "ACTIVE" : "inactive"));
                        return active;
                    }
                    finally
                    {
                        CloseHandle(hProcess);
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Kills every dwm.exe and waits until the replacement processes have loaded
        /// dwmcore.dll. Windows restarts DWM automatically within a second or so.
        ///
        /// Why this is needed for the EOTF fix: DWM builds its pixel shader objects from the
        /// DXBC blobs in dwmcore.dll during startup. Once those objects exist, patching the
        /// bytecode has no effect - which is why patching a long-running DWM did nothing.
        /// ledoge's dwm_eotf solves it the same way: terminate DWM, then patch the fresh
        /// process as soon as dwmcore.dll appears, well before the shaders are created.
        ///
        /// Costs a brief black flash. Only used when the EOTF fix is being turned on or off.
        /// </summary>
        private static readonly System.Diagnostics.Stopwatch _sinceLastRestart = new System.Diagnostics.Stopwatch();
        private const int MinRestartIntervalMs = 6000;

        /// <summary>
        /// Milliseconds still to wait before another DWM restart is allowed, 0 when ready. The GUI
        /// disables the gamma buttons while this is non-zero, so the wait is visible instead of
        /// being a frozen window - and, more importantly, so repeat clicks cannot queue up into a
        /// burst of restarts.
        /// </summary>
        public static int RestartCooldownRemainingMs
        {
            get
            {
                if (!_sinceLastRestart.IsRunning) return 0;
                var left = MinRestartIntervalMs - (int)_sinceLastRestart.ElapsedMilliseconds;
                return left > 0 ? left : 0;
            }
        }

        public static void RestartDwmAndWait(int timeoutMs = 10000)
        {
            // Only restart DWM for the session we are actually running in. Other sessions' DWM
            // belongs to other logged-in users, and killing it would blank their desktop for no
            // reason. (Injection enumerates every instance, but injecting is far less disruptive
            // than killing.)
            // Restarting DWM tears down and rebuilds the whole display pipeline. Doing it several
            // times in quick succession has been observed to leave a multi-monitor setup in a bad
            // state (a display dropping out, scaling and HDR reset), so consecutive restarts are
            // spaced out. This is a deliberate, user-initiated action, so waiting is acceptable.
            // The GUI disables the gamma buttons for the whole cooldown, so this should normally be
            // a no-op. It stays as a backstop for any other caller (hotkey, automation) - capped, so
            // it can never freeze the UI thread for the full window.
            var remaining = RestartCooldownRemainingMs;
            if (remaining > 0)
            {
                var wait = Math.Min(remaining, 1500);
                GuiDiag.Log("[restart] cooldown backstop: " + remaining + "ms remaining, waiting " + wait + "ms");
                System.Threading.Thread.Sleep(wait);
            }
            _sinceLastRestart.Restart();

            var mySession = Process.GetCurrentProcess().SessionId;
            GuiDiag.Log("[restart] killing dwm.exe in session " + mySession);

            var oldPids = new HashSet<int>();
            foreach (var p in Process.GetProcessesByName("dwm"))
            {
                try
                {
                    if (p.SessionId != mySession) continue;
                }
                catch { continue; }

                oldPids.Add(p.Id);
                try
                {
                    p.Kill();
                    GuiDiag.Log("[restart]   killed pid=" + p.Id);
                }
                catch (Exception ex)
                {
                    GuiDiag.LogError("RestartDwmAndWait/Kill pid=" + p.Id, ex);
                }
            }

            if (oldPids.Count == 0)
            {
                GuiDiag.Log("[restart] no dwm.exe found in this session - nothing to restart");
                return;
            }

            // Wait for replacement processes that have dwmcore.dll mapped. Injecting before
            // that point would fail to find the module to patch.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                System.Threading.Thread.Sleep(100);

                var fresh = 0;
                foreach (var p in Process.GetProcessesByName("dwm"))
                {
                    if (oldPids.Contains(p.Id)) continue;
                    try
                    {
                        foreach (ProcessModule m in p.Modules)
                        {
                            if (string.Equals(m.ModuleName, "dwmcore.dll", StringComparison.OrdinalIgnoreCase))
                            {
                                fresh++;
                                break;
                            }
                        }
                    }
                    catch { /* module list not ready yet */ }
                }

                if (fresh >= oldPids.Count)
                {
                    GuiDiag.Log("[restart] " + fresh + " fresh dwm.exe with dwmcore.dll after " +
                                sw.ElapsedMilliseconds + "ms; settling");
                    // dwmcore.dll is mapped, but the display stack is still settling: adapters may
                    // still be re-enumerating and the topology is briefly unqueryable. A short
                    // settle here costs nothing (the shaders are not built yet) and avoids
                    // hammering a graphics stack that is mid-reset.
                    System.Threading.Thread.Sleep(250);
                    return;
                }
            }

            GuiDiag.Log("[restart] TIMEOUT after " + sw.ElapsedMilliseconds +
                        "ms waiting for dwm.exe to come back with dwmcore.dll loaded");
        }

        /// <param name="includeLuts">
        /// When false, no .cube files are staged, so the DLL loads and applies the EOTF patch
        /// but no LUT. Lets the gamma fix be used without turning the LUT on.
        /// </param>
        /// <summary>
        /// Ordinary LUT apply: stage and inject in one go. No DWM restart is involved, so the
        /// ordering does not matter here.
        /// </summary>
        public static void Inject(IEnumerable<MonitorData> monitors, bool includeLuts = true)
        {
            StageForInject(monitors, includeLuts);
            InjectStaged();
        }

        /// <summary>
        /// Removes the staging folder, tolerating a file that is momentarily still open.
        ///
        /// `Directory.Delete` here used to be able to take the whole app down with
        /// "The process cannot access the file '0_0.cube' because it is being used by another
        /// process". The staged .cube files are read by dwm.exe and are also freshly-written files
        /// in a temp directory, so a real-time AV scanner or a compositor still finishing with them
        /// can hold a handle for a moment. This is a temp folder - failing to remove it is never
        /// worth an unhandled exception.
        ///
        /// If the folder itself cannot go, the individual files are removed best-effort so a stale
        /// LUT from a previous run can never be picked up by the next injection.
        /// </summary>
        private static void ClearLutsFolder()
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (!Directory.Exists(LutsPath)) return;
                    Directory.Delete(LutsPath, true);
                    return;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException)
                {
                    System.Threading.Thread.Sleep(50);
                }
            }

            var stragglers = 0;
            try
            {
                foreach (var f in Directory.GetFiles(LutsPath))
                {
                    try { File.Delete(f); }
                    catch { stragglers++; }
                }
            }
            catch { }

            GuiDiag.Log("[stage] LUT folder still in use after retries; " +
                        (stragglers == 0 ? "contents cleared instead" : stragglers + " file(s) left behind"));
        }

        /// <summary>
        /// Stages everything the injected DLL will need: a private copy of dwm_lut.dll and the
        /// per-monitor .cube files, with their DACLs cleared so DWM (running as SYSTEM) can read
        /// them. Nothing here depends on DWM, so when a restart is involved this must be done
        /// BEFORE killing it - otherwise all of this file and ACL work sits inside the window
        /// where DWM is starting up, and a failure would leave the desktop already torn down.
        /// </summary>
        public static void StageForInject(IEnumerable<MonitorData> monitors, bool includeLuts = true)
        {
            ElevatePrivilege();

            bool copied = false;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    File.Copy(AppDomain.CurrentDomain.BaseDirectory + DllName, DllPath, true);
                    copied = true;
                    break;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(50);
                }
            }

            if (!copied)
            {
                // The previous instance is still mapped into dwm.exe, so the file is locked. That is
                // routine during a display change, where uninject and re-inject happen back to back -
                // and it must not take the app down, which is what letting the copy throw did.
                //
                // If what is already staged is byte-identical to what we would have written, it is the
                // same build and injecting it is exactly what we were about to do anyway.
                var src = new FileInfo(AppDomain.CurrentDomain.BaseDirectory + DllName);
                var dst = new FileInfo(DllPath);

                if (dst.Exists && src.Exists &&
                    dst.Length == src.Length && dst.LastWriteTimeUtc == src.LastWriteTimeUtc)
                {
                    GuiDiag.Log("[stage] dwm_lut.dll still in use; staged copy is identical, reusing it");
                }
                else
                {
                    // Genuinely different builds: retry a while longer before giving up, since the
                    // lock is transient and injecting a stale DLL would be worse than waiting.
                    for (var attempt = 0; attempt < 40 && !copied; attempt++)
                    {
                        try
                        {
                            File.Copy(AppDomain.CurrentDomain.BaseDirectory + DllName, DllPath, true);
                            copied = true;
                        }
                        catch (IOException) { System.Threading.Thread.Sleep(50); }
                    }

                    if (!copied)
                    {
                        GuiDiag.Log("[stage] could not replace dwm_lut.dll - still locked by dwm.exe");
                        return;   // skip this injection; the next display change will retry
                    }
                }
            }

            ClearPermissions(DllPath);

            ClearLutsFolder();

            Directory.CreateDirectory(LutsPath);
            ClearPermissions(LutsPath);

            foreach (var monitor in includeLuts ? monitors : Enumerable.Empty<MonitorData>())
            {
                if (!string.IsNullOrEmpty(monitor.SdrLutPath))
                {
                    var dest = LutsPath + monitor.Position.Replace(',', '_') + ".cube";
                    CopyOrConvertLut(monitor.SdrLutPath, dest);
                }

                if (string.IsNullOrEmpty(monitor.HdrLutPath)) continue;
                {
                    var dest = LutsPath + monitor.Position.Replace(',', '_') + "_hdr.cube";
                    CopyOrConvertLut(monitor.HdrLutPath, dest);
                }
            }

        }

        /// <summary>
        /// Loads the already-staged DLL into every dwm.exe and removes the staging directory.
        /// Split from <see cref="StageForInject"/> so the slow part can happen before a DWM
        /// restart while this, the only part that needs a live DWM, happens after it.
        /// </summary>
        public static void InjectStaged()
        {
            ElevatePrivilege();

            var failed = false;
            var bytes = Encoding.ASCII.GetBytes(DllPath);
            var dwmInstances = Process.GetProcessesByName("dwm");
            foreach (var dwm in dwmInstances)
            {
                var address = VirtualAllocEx(dwm.Handle, IntPtr.Zero, (UIntPtr)bytes.Length,
                    AllocationType.Reserve | AllocationType.Commit, MemoryProtection.ReadWrite);
                WriteProcessMemory(dwm.Handle, address, bytes, (UIntPtr)bytes.Length, out _);
                var thread = CreateRemoteThread(dwm.Handle, IntPtr.Zero, 0, LoadlibraryA, address, 0, out _);
                WaitForSingleObject(thread, uint.MaxValue);

                GetExitCodeThread(thread, out var exitCode);
                if (exitCode == 0)
                {
                    failed = true;
                }

                CloseHandle(thread);
                VirtualFreeEx(dwm.Handle, address, 0, FreeType.Release);

                dwm.Dispose();
            }

            ClearLutsFolder();

            if (!failed)
            {
                RevertToSelf();
                return;
            }

            File.Delete(DllPath);

            RevertToSelf();

            throw new Exception(
                "Failed to load or initialize DLL. This probably means that a LUT file is malformed or that DWM got updated.");
        }

        /// <summary>
        /// Applies the SDR-in-HDR gamma fix to every dwm.exe by patching dwmcore's shader
        /// bytecode from outside the process, with DWM suspended. Call immediately after
        /// <see cref="RestartDwmAndWait"/>, before DWM has created its pixel shaders.
        /// Returns the total number of shader sites patched (0 = nothing changed).
        /// </summary>
        public static int ApplyEotfPatch(double gamma)
        {
            var elevated = false;
            try
            {
                ElevatePrivilege();
                elevated = true;
            }
            catch { }

            var total = 0;
            GuiDiag.Log("[eotf] applying gamma " + gamma.ToString("0.00") + " (elevated=" + elevated + ")");
            try
            {
                foreach (var dwm in Process.GetProcessesByName("dwm"))
                {
                    try
                    {
                        total += EotfPatcher.PatchProcess(dwm, (float)gamma);
                    }
                    catch (Exception ex)
                    {
                        GuiDiag.LogError("ApplyEotfPatch/PatchProcess pid=" + dwm.Id, ex);
                    }
                    finally
                    {
                        dwm.Dispose();
                    }
                }
            }
            finally
            {
                if (elevated) RevertToSelf();
            }

            GuiDiag.Log("[eotf] total sites patched across all dwm.exe: " + total);
            _statusAge.Reset();   // force a fresh read next time we are asked
            return total;
        }

        public static void Uninject()
        {
            bool elevated = false;
            try
            {
                ElevatePrivilege();
                elevated = true;
            }
            catch { }

            var dwmInstances = Process.GetProcessesByName("dwm");
            foreach (var dwm in dwmInstances)
            {
                System.Diagnostics.ProcessModuleCollection modules = null;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        modules = dwm.Modules;
                        break;
                    }
                    catch
                    {
                        System.Threading.Thread.Sleep(100);
                    }
                }

                if (modules != null)
                {
                    foreach (ProcessModule module in modules)
                    {
                        try
                        {
                            if (module.ModuleName == DllName)
                            {
                                var thread = CreateRemoteThread(dwm.Handle, IntPtr.Zero, 0, FreeLibrary, module.BaseAddress,
                                    0, out _);
                                WaitForSingleObject(thread, uint.MaxValue);
                                CloseHandle(thread);
                            }
                        }
                        catch { }
                        finally
                        {
                            module.Dispose();
                        }
                    }
                }

                dwm.Dispose();
            }

            if (elevated)
            {
                RevertToSelf();
            }

            try
            {
                File.Delete(DllPath);
            }
            catch { }

        }

        private static void ClearPermissions(string path)
        {
            var hFile = CreateFile(path, DesiredAccess.ReadControl | DesiredAccess.WriteDac, 0, IntPtr.Zero,
                CreationDisposition.OpenExisting,
                FlagsAndAttributes.FileAttributeNormal | FlagsAndAttributes.FileFlagBackupSemantics,
                IntPtr.Zero);
            SetSecurityInfo(hFile, SeObjectType.SeFileObject, SecurityInformation.DaclSecurityInformation, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            CloseHandle(hFile);
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpFileName);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll")]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize,
            AllocationType flAllocationType, MemoryProtection flProtect);

        [DllImport("kernel32.dll")]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer,
            UIntPtr nSize,
            out UIntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll")]
        private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, int dwSize, FreeType dwFreeType);

        [DllImport("kernel32.dll")]
        private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize,
            IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll")]
        private static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);

        [DllImport("kernel32.dll")]
        private static extern IntPtr CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(DesiredAccess dwDesiredAccess, bool bInheritHandle,
                       uint dwProcessId);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, DesiredAccess desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ImpersonateLoggedOnUser(IntPtr hToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetUserName(StringBuilder lpBuffer, ref uint nSize);


        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool RevertToSelf();

        [DllImport("kernel32.dll")]
        private static extern IntPtr CreateFile(string lpFileName, DesiredAccess dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, CreationDisposition dwCreationDisposition,
            FlagsAndAttributes dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("advapi32.dll")]
        private static extern uint SetSecurityInfo(IntPtr handle, SeObjectType ObjectType,
            SecurityInformation SecurityInfo, IntPtr psidOwner,
            IntPtr psidGroup, IntPtr pDacl, IntPtr pSacl);

        [Flags]
        private enum FreeType
        {
            Release = 0x8000,
        }

        [Flags]
        private enum AllocationType
        {
            Commit = 0x1000,
            Reserve = 0x2000
        }

        [Flags]
        private enum MemoryProtection
        {
            ReadWrite = 0x04
        }

        [Flags]
        private enum DesiredAccess
        {
            ReadControl = 0x20000,
            WriteDac = 0x40000,
            ProcessQueryLimitedInformation = 0x1000,
            MaximumAllowed = 0x02000000
        }

        private enum CreationDisposition
        {
            OpenExisting = 3
        }

        [Flags]
        private enum FlagsAndAttributes
        {
            FileAttributeNormal = 0x80,
            FileFlagBackupSemantics = 0x2000000
        }

        private enum SeObjectType
        {
            SeFileObject = 1
        }

        private enum SecurityInformation
        {
            DaclSecurityInformation = 0x4
        }
    }
}