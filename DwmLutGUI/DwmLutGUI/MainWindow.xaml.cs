using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using Microsoft.Win32;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Net;
using System.Text.RegularExpressions;
using ContextMenu = System.Windows.Forms.ContextMenu;
using MenuItem = System.Windows.Forms.MenuItem;
using MessageBox = System.Windows.Forms.MessageBox;

namespace DwmLutGUI
{
    public partial class MainWindow
    {
        private readonly MainViewModel _viewModel;
        private bool _applyOnCooldown;
        private bool _isExiting;
        // Active composition-blocker overlays, keyed by monitor origin "X,Y". One per monitor that is
        // BOTH LUT-active AND currently covered by a fullscreen app.
        private readonly Dictionary<string, OverlayWindow> _blockers = new Dictionary<string, OverlayWindow>();
        private System.Windows.Threading.DispatcherTimer _blockerTimer;

        // --- Composition-blocker monitor enumeration in TRUE physical pixels ---
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        // Per-Monitor-v2 DPI context: makes EnumDisplayMonitors rects and SetWindowPos coordinates true
        // physical pixels regardless of the process's (system-DPI-aware) manifest. Windows 10 1607+.
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        // --- Per-monitor fullscreen detection (which monitor is covered by a fullscreen app) ---
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
        private const int DWMWA_CLOAKED = 14;

        // --- Lightweight Apply-path trace (diagnoses the rare "LUT wipes in on cursor movement" issue) ---
        // Writes to %TEMP%\dwmlut_apply.log. Flip EnableApplyDiag to true to re-enable when investigating.
        private const bool EnableApplyDiag = false;

        private static class ApplyDiag
        {
            private static readonly object Lock = new object();
            private static readonly string LogPath =
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dwmlut_apply.log");
            private static readonly System.Diagnostics.Stopwatch Sw = new System.Diagnostics.Stopwatch();

            public static void Mark() { if (EnableApplyDiag) Sw.Restart(); }

            public static void Log(string msg)
            {
                if (!EnableApplyDiag) return;
                try
                {
                    var line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] [+" +
                               Sw.ElapsedMilliseconds.ToString().PadLeft(5) + "ms] " + msg + "\r\n";
                    lock (Lock) System.IO.File.AppendAllText(LogPath, line);
                }
                catch { /* diagnostics must never disturb the app */ }
            }
        }

        // Enumerate TRUE physical monitor rects (per-monitor-v2 context) on a throwaway thread, so the UI
        // thread's DPI awareness is never touched (changing it disturbs WPF's redraw on scaled monitors).
        // Screen.Bounds cannot be used: in a system-DPI-aware process it is expressed in the PRIMARY
        // monitor's DPI units, so it is wrong on every differently-scaled monitor.
        private static List<RECT> GetPhysicalMonitorRects()
        {
            var rects = new List<RECT>();
            try
            {
                var th = new Thread(() =>
                {
                    try { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
                    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                        (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) => { rects.Add(r); return true; }, IntPtr.Zero);
                }) { IsBackground = true };
                th.Start();
                th.Join(500);
            }
            catch { }
            return rects;
        }

        private readonly MenuItem _statusItem;
        private readonly MenuItem _applyItem;
        private readonly MenuItem _disableItem;
        private readonly MenuItem _disableAndExitItem;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public MainWindow()
        {
            try
            {
                if (Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Length > 1)
                {
                    MessageBox.Show("Already running!");
                    Close();
                    return;
                }

                InitializeComponent();
                ApplyDarkMode();
                _viewModel = (MainViewModel)DataContext;
                _applyOnCooldown = false;

                var args = Environment.GetCommandLineArgs().ToList();
                args.RemoveAt(0);

                if (args.Contains("-apply"))
                {
                    Apply_Click(null, null);
                }
                else if (args.Contains("-disable"))
                {
                    Disable_Click(null, null);
                }

                if (args.Contains("-minimize"))
                {
                    WindowState = WindowState.Minimized;
                    Hide();
                }
                else if (args.Contains("-exit"))
                {
                    Close();
                    return;
                }

                var notifyIcon = new NotifyIcon();
                var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DwmLutGUI.smile.ico");
                notifyIcon.Icon = new Icon(stream);
                notifyIcon.Visible = true;
                notifyIcon.DoubleClick +=
                    delegate
                    {
                        Show();
                        WindowState = WindowState.Normal;
                    };

                var contextMenu = new ContextMenu();

                _statusItem = new MenuItem();
                contextMenu.MenuItems.Add(_statusItem);
                _statusItem.Enabled = false;

                contextMenu.MenuItems.Add("-");

                _applyItem = new MenuItem();
                contextMenu.MenuItems.Add(_applyItem);
                _applyItem.Text = "Apply";
                _applyItem.Click += delegate { Apply_Click(null, null); };

                _disableItem = new MenuItem();
                contextMenu.MenuItems.Add(_disableItem);
                _disableItem.Text = "Disable";
                _disableItem.Click += delegate { Disable_Click(null, null); };

                contextMenu.MenuItems.Add("-");

                _disableAndExitItem = new MenuItem();
                contextMenu.MenuItems.Add(_disableAndExitItem);
                _disableAndExitItem.Text = "Disable and exit";
                _disableAndExitItem.Click += delegate
                {
                    Disable_Click(null, null);
                    _isExiting = true;   // without this, MainWindow_Closing cancels the close and hides to tray
                    Close();
                };

                var exitItem = new MenuItem();
                contextMenu.MenuItems.Add(exitItem);
                exitItem.Text = "Exit";
                exitItem.Click += delegate { 
                    _isExiting = true;
                    Close(); 
                };

                contextMenu.Popup += delegate { UpdateContextMenu(); };

                notifyIcon.ContextMenu = contextMenu;

                notifyIcon.Text = Assembly.GetEntryAssembly().GetName().Name;

                Closed += delegate
                {
                    CloseCompositionBlockers();
                    notifyIcon.Dispose();
                };

                SystemEvents.DisplaySettingsChanged += _viewModel.OnDisplaySettingsChanged;
                // Rebuild composition blockers for the new monitor layout (attach/detach, resolution or
                // DPI change) once the ViewModel has refreshed its monitor list. Deferred via the
                // dispatcher so Monitors is already up to date when we re-scope.
                SystemEvents.DisplaySettingsChanged += (s, e) =>
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_viewModel.IsActive) StartCompositionBlockers();
                        else CloseCompositionBlockers();
                    }));
                App.KListener.KeyDown += MonitorLutToggle;
                var keys = Enum.GetValues(typeof(Key)).Cast<Key>().ToList();
                ToggleKeyCombo.ItemsSource = keys;
                // The SelectedItem binding evaluated during InitializeComponent, before ItemsSource existed,
                // so the current key wouldn't display. Re-assert it now that the items are present (shows
                // the actual key, or "None" when no hotkey is set, instead of a blank box).
                ToggleKeyCombo.SelectedItem = _viewModel.ToggleKey;

                Closing += MainWindow_Closing;
                CheckAutostart();
                CheckForUpdates();
            }
            catch (Exception ex)
            {
                MessageBox.Show("MainWindow init crash:\n\n" + ex.ToString(), "DwmLutGUI Error");
                _isExiting = true;   // an init failure should exit, not hide to tray (Closing may be wired up by now)
                Close();
            }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
            }

            base.OnStateChanged(e);
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExiting)
            {
                e.Cancel = true;
                Hide();
            }
        }

        private void CheckAutostart()
        {
            if (_viewModel.AutostartAsked) return;

            var result = MessageBox.Show(
                "Hi! Would you like DwmLut to start automatically with Windows?\n\nThis ensures your LUTs are applied as soon as you log in.",
                "Autostart",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == System.Windows.Forms.DialogResult.Yes)
            {
                SetAutostart(true);
            }
            
            _viewModel.AutostartAsked = true;
        }

        private void SetAutostart(bool enable)
        {
            try
            {
                
                string runKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(runKeyPath, true))
                {
                    key?.DeleteValue("DwmLutGUI", false);
                }

                string taskName = "DwmLutGUI_Autostart";
                string exePath = Assembly.GetExecutingAssembly().Location;
                
                if (exePath.EndsWith(".dll")) exePath = exePath.Replace(".dll", ".exe");

                if (enable)
                {
                    
                    
                    string args = $"/create /tn \"{taskName}\" /tr \"\\\"{exePath}\\\" -apply -minimize\" /sc onlogon /rl highest /f";
                    
                    ProcessStartInfo psi = new ProcessStartInfo("schtasks", args)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = true,
                        Verb = "runas", 
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(psi);
                }
                else
                {
                    
                    string args = $"/delete /tn \"{taskName}\" /f";
                    ProcessStartInfo psi = new ProcessStartInfo("schtasks", args)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                
                if (!(ex is System.ComponentModel.Win32Exception))
                {
                    MessageBox.Show("Error managing autostart task: " + ex.Message);
                }
            }
        }

        private void UpdateContextMenu()
        {
            _statusItem.Text = "Status: " + _viewModel.ActiveText;

            var canDisable = _viewModel.IsActive && !Injector.NoDebug;

            _applyItem.Enabled = _viewModel.CanApply;
            _disableItem.Enabled = canDisable;
            _disableAndExitItem.Enabled = canDisable;
        }

        private static string BrowseLuts(string folder)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "LUT Files|*.cube;*.txt",
                InitialDirectory = folder
            };

            var result = dlg.ShowDialog();

            return result == true ? dlg.FileName : null;
        }

        private void CheckForUpdates()
        {
            Task.Run(() =>
            {
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)3072; // TLS 1.2 + 1.3
                    using (var client = new WebClient())
                    {
                        string content = client.DownloadString("https://raw.githubusercontent.com/zkippp/dwm_lut_fixed/master/README.md");
                        var match = Regex.Match(content, @"Current Version: ([v\d\.\w_]+)");
                        if (match.Success)
                        {
                            string latestTag = match.Groups[1].Value;
                            string remoteVerStr = latestTag.TrimStart('v').Split('_')[0];
                            
                            if (Version.TryParse(remoteVerStr, out Version remoteVersion))
                            {
                                if (remoteVersion > Assembly.GetExecutingAssembly().GetName().Version)
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        var result = System.Windows.MessageBox.Show(
                                            $"A new version is available: {latestTag}\n\nWould you like to download it now?",
                                            "Update Available",
                                            System.Windows.MessageBoxButton.YesNo,
                                            System.Windows.MessageBoxImage.Information);

                                        if (result == System.Windows.MessageBoxResult.Yes)
                                        {
                                            Process.Start(new ProcessStartInfo($"https://github.com/zkippp/dwm_lut_fixed/releases/tag/{latestTag}") { UseShellExecute = true });
                                        }
                                    });
                                }
                            }
                        }
                    }
                }
                catch { }
            });
        }

        private void AboutButton_Click(object sender, RoutedEventArgs o)
        {
            var window = new AboutWindow
            {
                Owner = this
            };
            window.ShowDialog();
        }

        private void SdrLutBrowse_Click(object sender, RoutedEventArgs e)
        {
            var folder = Path.GetDirectoryName(_viewModel.SdrLutPath);
            var lutPath = BrowseLuts(folder);
            if (!string.IsNullOrEmpty(lutPath))
            {
                _viewModel.SdrLutPath = lutPath;
                WarnIfLutModeMismatch(false);
            }
        }

        private void SdrLutClear_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.SdrLutPath = "None";
        }

        private void HdrLutBrowse_Click(object sender, RoutedEventArgs e)
        {
            var folder = Path.GetDirectoryName(_viewModel.HdrLutPath);
            var lutPath = BrowseLuts(folder);
            if (!string.IsNullOrEmpty(lutPath))
            {
                _viewModel.HdrLutPath = lutPath;
                WarnIfLutModeMismatch(true);
            }
        }

        // Warn if the just-assigned LUT can't apply in the display's current mode. The DLL only applies a
        // LUT whose type matches the composition (HDR LUT for an HDR display, SDR LUT for an SDR display);
        // a mismatched LUT is silently ignored, so surface that here instead of leaving the user puzzled.
        private void WarnIfLutModeMismatch(bool isHdrLut)
        {
            var m = _viewModel.SelectedMonitor;
            if (m == null) return;
            if (isHdrLut && !m.IsHdr)
                MessageBox.Show(
                    "This display is currently in SDR mode.\n\nAn HDR LUT is only applied while the display is in HDR mode, so it will have no effect right now. Assign an SDR LUT for SDR mode, or enable HDR for this display in Windows display settings.",
                    "LUT / display-mode mismatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else if (!isHdrLut && m.IsHdr)
                MessageBox.Show(
                    "This display is currently in HDR mode.\n\nAn SDR LUT is only applied while the display is in SDR mode, so it will have no effect right now. Assign an HDR LUT for HDR mode, or disable HDR for this display in Windows display settings.",
                    "LUT / display-mode mismatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void HdrLutClear_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.HdrLutPath = "None";
        }

        private void Disable_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _viewModel.Uninject();
                CloseCompositionBlockers();
                RedrawScreens();
            }
            catch (Exception x)
            {
                MessageBox.Show(x.Message);
            }
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (_applyOnCooldown) return;
            _applyOnCooldown = true;

            ApplyDiag.Mark();
            ApplyDiag.Log("=== Apply_Click begin ===");
            try
            {
                _viewModel.ReInject();
                ApplyDiag.Log("ReInject returned, IsActive=" + _viewModel.IsActive);
                if (_viewModel.IsActive)
                {
                    StartCompositionBlockers();
                }
                else
                {
                    CloseCompositionBlockers();
                }
                ApplyDiag.Log("blockers handled, active overlay count=" + _blockers.Count);
                RedrawScreens();
                ApplyDiag.Log("RedrawScreens returned");
            }
            catch (Exception x)
            {
                ApplyDiag.Log("EXCEPTION: " + x.Message);
                MessageBox.Show(x.Message);
            }
            ApplyDiag.Log("=== Apply_Click end ===");

            Task.Run(() =>
            {
                Thread.Sleep(100);
                _applyOnCooldown = false;
            });
        }

        private static void RedrawScreens()
        {
            // Force DWM to fully re-composite each output so the LUT lands everywhere immediately, instead
            // of leaving static regions with pre-LUT pixels until the user dirties them (the "wipe-in").
            // Position per monitor in TRUE physical pixels -- Screen.Bounds is anchored to the primary's
            // DPI and is wrong on mixed-DPI layouts, which made the old single union window miss monitors.
            var rects = GetPhysicalMonitorRects();
            ApplyDiag.Log("RedrawScreens: " + rects.Count + " physical monitor(s)");

            var overlays = new List<OverlayWindow>();
            foreach (var r in rects)
            {
                var overlay = new OverlayWindow();
                overlay.Show();
                overlay.PositionPhysical(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                overlays.Add(overlay);
                ApplyDiag.Log("  repaint L" + r.Left + " T" + r.Top +
                              " W" + (r.Right - r.Left) + " H" + (r.Bottom - r.Top));
            }

            if (overlays.Count == 0)   // enumeration failed -> old union fallback so we still redraw something
            {
                var rect = Screen.AllScreens.Select(x => x.Bounds).Aggregate(Rectangle.Union);
                var overlay = new OverlayWindow { Left = rect.Left, Top = rect.Top, Height = rect.Height, Width = rect.Width };
                overlay.Show();
                overlay.ForceTopmostNoActivate();
                overlays.Add(overlay);
                ApplyDiag.Log("  (fallback) union redraw L" + rect.Left + " T" + rect.Top + " W" + rect.Width + " H" + rect.Height);
            }

            Thread.Sleep(50);
            foreach (var o in overlays)
            {
                try { o.Close(); } catch { }
            }
            ApplyDiag.Log("  redraw overlays shown ~50ms then closed");
        }

        // Called when LUTs become active: start watching for per-monitor fullscreen and maintain the
        // blocker overlays. The overlay set itself is computed in RefreshCompositionBlockers().
        private void StartCompositionBlockers()
        {
            if (_blockerTimer == null)
            {
                _blockerTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(400)
                };
                _blockerTimer.Tick += (s, e) => RefreshCompositionBlockers();
            }
            _blockerTimer.Start();
            RefreshCompositionBlockers();   // immediate first pass, don't wait a tick
        }

        // Reconciles the overlay set to exactly: monitors that are BOTH LUT-active AND currently covered
        // by a fullscreen app. Adds/removes individual overlays as that intersection changes.
        private void RefreshCompositionBlockers()
        {
            if (_isExiting || !_viewModel.IsActive) { CloseCompositionBlockers(); return; }

            // Origins ("X,Y") of monitors whose assigned LUT matches their current mode (so it actually
            // applies -- an SDR LUT on an HDR display, or vice versa, does not, and counts as inactive).
            var lutOrigins = new HashSet<string>();
            foreach (var m in _viewModel.Monitors)
                if (!string.IsNullOrEmpty(m.Position) && HasApplicableLut(m))
                    lutOrigins.Add(m.Position);

            var fullscreen = new HashSet<string>();
            if (lutOrigins.Count > 0)
            {
                IntPtr prevCtx = IntPtr.Zero;
                var haveCtx = false;
                try { prevCtx = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); haveCtx = true; }
                catch { /* pre-1607 Windows: fall back to the process default awareness */ }
                try
                {
                    var monitorRects = new List<RECT>();
                    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                        (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) => { monitorRects.Add(r); return true; },
                        IntPtr.Zero);

                    // Fullscreen only matters on monitors whose LUT actually applies.
                    var lutRects = monitorRects.Where(r => lutOrigins.Contains(r.Left + "," + r.Top)).ToList();
                    fullscreen = GetFullscreenMonitorOrigins(lutRects);

                    // Desired overlays = applicable-LUT AND fullscreen.
                    var desired = new Dictionary<string, RECT>();
                    foreach (var r in lutRects)
                    {
                        var key = r.Left + "," + r.Top;
                        if (fullscreen.Contains(key)) desired[key] = r;
                    }

                    foreach (var key in _blockers.Keys.ToArray())      // remove ones no longer wanted
                        if (!desired.ContainsKey(key)) CloseBlocker(key);
                    foreach (var kv in desired)                        // add ones newly wanted
                        if (!_blockers.ContainsKey(kv.Key)) CreateBlocker(kv.Key, kv.Value);
                }
                finally
                {
                    if (haveCtx) { try { SetThreadDpiAwarenessContext(prevCtx); } catch { } }
                }
            }
            else
            {
                // Active but nothing applicable -> ensure no blockers remain.
                foreach (var key in _blockers.Keys.ToArray()) CloseBlocker(key);
            }

            // Push a live Status onto every row (not just the ones that got a blocker).
            foreach (var m in _viewModel.Monitors)
                m.Status = ComputeStatus(m, fullscreen);
        }

        // A monitor's LUT is "applicable" only if it matches the display's current mode -- the DLL applies
        // an HDR LUT to an HDR context and an SDR LUT to an SDR context, nothing crossed.
        private static bool HasApplicableLut(MonitorData m) =>
            m.IsHdr ? !string.IsNullOrEmpty(m.HdrLutPath) : !string.IsNullOrEmpty(m.SdrLutPath);

        // Per-row Status label. Only called while the tool is active (RefreshCompositionBlockers returns
        // early otherwise, and CloseCompositionBlockers sets every row to "Inactive").
        private static string ComputeStatus(MonitorData m, HashSet<string> fullscreenOrigins)
        {
            if (!HasApplicableLut(m)) return "Inactive";
            var fs = !string.IsNullOrEmpty(m.Position) && fullscreenOrigins.Contains(m.Position);
            return fs ? "Active, fullscreen mode" : "Active, windowed mode";
        }

        private void CreateBlocker(string key, RECT r)
        {
            // Full-monitor, click-through, almost-transparent (non-zero alpha), topmost surface. A fully
            // transparent (Opacity=0) window may be optimized away and then does not block promotion; the
            // 1/255 alpha is deliberate. It must cover the whole monitor because Firefox's DirectComposition
            // video path can present the video as a hardware overlay plane a small keepalive would miss.
            var overlay = new OverlayWindow
            {
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            overlay.Show();
            overlay.PositionPhysical(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            _blockers[key] = overlay;
            ApplyDiag.Log("blocker CREATED for monitor " + key + " (W" + (r.Right - r.Left) + " H" + (r.Bottom - r.Top) + ")");
        }

        private void CloseBlocker(string key)
        {
            if (_blockers.TryGetValue(key, out var overlay))
            {
                try { overlay.Close(); } catch { /* ignore stale windows */ }
                _blockers.Remove(key);
                ApplyDiag.Log("blocker CLOSED for monitor " + key);
            }
        }

        // Which of the given (LUT-active) monitors are currently covered by a fullscreen app: the topmost
        // non-shell content window on the monitor spans the whole monitor. Per-monitor and foreground-
        // independent. Our own overlays are excluded so they can never keep themselves alive.
        private HashSet<string> GetFullscreenMonitorOrigins(List<RECT> monitorRects)
        {
            var result = new HashSet<string>();
            if (monitorRects.Count == 0) return result;

            var ownHwnds = new HashSet<IntPtr>();
            foreach (var ov in _blockers.Values)
            {
                var h = new WindowInteropHelper(ov).Handle;
                if (h != IntPtr.Zero) ownHwnds.Add(h);
            }

            var shell = GetShellWindow();
            var desktop = GetDesktopWindow();
            var decided = new bool[monitorRects.Count];
            var remaining = monitorRects.Count;

            EnumWindows((hwnd, l) =>
            {
                if (remaining == 0) return false;                       // all monitors resolved
                if (ownHwnds.Contains(hwnd) || hwnd == shell || hwnd == desktop) return true;
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd)) return true;
                var cls = GetWindowClass(hwnd);
                if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" || cls == "Progman" || cls == "WorkerW")
                    return true;                                        // shell/taskbar/desktop, not content
                if (!GetWindowRect(hwnd, out var wr)) return true;
                if (wr.Right - wr.Left <= 0 || wr.Bottom - wr.Top <= 0) return true;

                for (var i = 0; i < monitorRects.Count; i++)
                {
                    if (decided[i]) continue;
                    var m = monitorRects[i];
                    if (!RectIntersects(wr, m)) continue;

                    // Topmost content window on monitor i. If it covers the whole monitor, the monitor is
                    // "fullscreen"; otherwise a windowed app is on top (DWM already composites -> no blocker).
                    decided[i] = true;
                    remaining--;
                    if (RectCovers(wr, m)) result.Add(m.Left + "," + m.Top);
                }
                return remaining > 0;
            }, IntPtr.Zero);

            return result;
        }

        private static bool RectIntersects(RECT a, RECT b) =>
            a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

        private static bool RectCovers(RECT win, RECT mon) =>
            win.Left <= mon.Left && win.Top <= mon.Top && win.Right >= mon.Right && win.Bottom >= mon.Bottom;

        private static bool IsCloaked(IntPtr hwnd)
        {
            try { return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var c, sizeof(int)) == 0 && c != 0; }
            catch { return false; }
        }

        private static string GetWindowClass(IntPtr hwnd)
        {
            var sb = new System.Text.StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private void CloseCompositionBlockers()
        {
            _blockerTimer?.Stop();
            foreach (var key in _blockers.Keys.ToArray())
                CloseBlocker(key);
            _blockers.Clear();
            foreach (var m in _viewModel.Monitors)   // nothing is being applied -> every row is Inactive
                m.Status = "Inactive";
        }

        // Cycle THIS ROW's monitor to the next SDR LUT in its own list (wrapping). The button lives in the
        // row's cell, so its DataContext is that row's MonitorData -- no dependency on the selected row.
        // Does nothing if the monitor has fewer than 2 SDR LUTs (nothing to cycle to).
        private void SwitchSdrLut_Click(object sender, RoutedEventArgs e)
        {
            var monitor = (sender as FrameworkElement)?.DataContext as MonitorData;
            if (monitor == null || monitor.SdrLuts.Count < 2) return;
            var next = (monitor.SdrLuts.IndexOf(monitor.SdrLutPath) + 1) % monitor.SdrLuts.Count;
            monitor.SdrLutPath = monitor.SdrLuts[next];
            ReapplyIfActive();
        }

        private void MonitorLutToggle(object sender, RawKeyEventArgs e)
        {
            // Read the bound property (the source of truth), not the ComboBox selection: Key.None means
            // "no hotkey set", and casting an empty ComboBox selection to Key would throw on every keystroke.
            if (_viewModel.ToggleKey == Key.None || e.Key != _viewModel.ToggleKey) return;
            // Toggle LUTs on/off, mirroring the Apply / Disable buttons: Disable if currently active,
            // otherwise Apply (only when applying is actually possible, i.e. CanApply).
            if (_viewModel.IsActive)
                Disable_Click(null, null);
            else if (_viewModel.CanApply)
                Apply_Click(null, null);
        }

        // Cycle THIS ROW's monitor to the next HDR LUT in its own list (wrapping). Nothing happens with
        // fewer than 2 HDR LUTs.
        private void SwitchHdrLut_Click(object sender, RoutedEventArgs e)
        {
            var monitor = (sender as FrameworkElement)?.DataContext as MonitorData;
            if (monitor == null || monitor.HdrLuts.Count < 2) return;
            var next = (monitor.HdrLuts.IndexOf(monitor.HdrLutPath) + 1) % monitor.HdrLuts.Count;
            monitor.HdrLutPath = monitor.HdrLuts[next];
            ReapplyIfActive();
        }

        // If LUTs are currently active, re-apply so a just-switched LUT takes effect immediately (same
        // Disable-then-Apply the old hotkey cycle used).
        private void ReapplyIfActive()
        {
            if (!_viewModel.IsActive) return;
            Disable_Click(null, null);
            Apply_Click(null, null);
        }

        // Remove THIS ROW's current SDR LUT from its list. If that LUT is currently applied, unapply first
        // (and stay disabled), then fall back to the next LUT in the list (or "None").
        private void DeleteSdrLut_Click(object sender, RoutedEventArgs e)
        {
            var monitor = (sender as FrameworkElement)?.DataContext as MonitorData;
            if (monitor == null) return;
            var current = monitor.SdrLutPath;
            if (string.IsNullOrEmpty(current) || current == "None") return;   // nothing to remove

            if (_viewModel.IsActive) Disable_Click(null, null);               // unapply and stay disabled
            monitor.SdrLuts.Remove(current);
            monitor.SdrLutPath = monitor.SdrLuts.FirstOrDefault() ?? "None";  // fall back to next, or off
        }

        private void DeleteHdrLut_Click(object sender, RoutedEventArgs e)
        {
            var monitor = (sender as FrameworkElement)?.DataContext as MonitorData;
            if (monitor == null) return;
            var current = monitor.HdrLutPath;
            if (string.IsNullOrEmpty(current) || current == "None") return;

            if (_viewModel.IsActive) Disable_Click(null, null);               // unapply and stay disabled
            monitor.HdrLuts.Remove(current);
            monitor.HdrLutPath = monitor.HdrLuts.FirstOrDefault() ?? "None";
        }

        private void ApplyDarkMode()
        {
            IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
            int useDarkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
        }
    }

    // Shows just the file name of a LUT path in the LUT combo boxes; the bound value stays the full path.
    public class LutFileNameConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var s = value as string;
            return string.IsNullOrEmpty(s) ? s : (System.IO.Path.GetFileName(s) ?? s);
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value;
    }
}