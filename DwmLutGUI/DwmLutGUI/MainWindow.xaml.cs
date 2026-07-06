using System;
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

                Closed += delegate { notifyIcon.Dispose(); };

                SystemEvents.DisplaySettingsChanged += _viewModel.OnDisplaySettingsChanged;
                App.KListener.KeyDown += MonitorLutToggle;
                var keys = Enum.GetValues(typeof(Key)).Cast<Key>().ToList();
                ToggleKeyCombo.ItemsSource = keys;

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

            try
            {
                _viewModel.ReInject();
                RedrawScreens();
            }
            catch (Exception x)
            {
                MessageBox.Show(x.Message);
            }

            Task.Run(() =>
            {
                Thread.Sleep(100);
                _applyOnCooldown = false;
            });
        }

        private static void RedrawScreens()
        {
            var rect = Screen.AllScreens.Select(x => x.Bounds).Aggregate(Rectangle.Union);
            var overlay = new OverlayWindow
            {
                Left = rect.Left,
                Top = rect.Top,
                Height = rect.Height,
                Width = rect.Width,
            };

            overlay.Show();
            Thread.Sleep(50);
            overlay.Close();
        }

        private void RemoveSdrLut_Click(object sender, RoutedEventArgs e)
        {
            var monitor = _viewModel.SelectedMonitor;
            if (monitor == null) return;
            monitor.SdrLuts.Remove(monitor.SdrLutPath);
            var anySdrLut = monitor.SdrLuts.FirstOrDefault();
            monitor.SdrLutPath = anySdrLut ?? "None";
        }

        private void MonitorLutToggle(object sender, RawKeyEventArgs e)
        {
            if (e.Key != (Key)ToggleKeyCombo.SelectedItem) return;
            var monitor = _viewModel.SelectedMonitor;
            if (monitor == null) return;
            if (monitor.SdrLutFilename != "None")
            {
                _viewModel.SdrLutPath =
                    monitor.SdrLuts[(monitor.SdrLuts.IndexOf(monitor.SdrLutPath) + 1) % monitor.SdrLuts.Count];
            }
            else
            {
                _viewModel.HdrLutPath = monitor.HdrLuts[(monitor.HdrLuts.IndexOf(monitor.HdrLutPath) + 1) % monitor.HdrLuts.Count];
            }

            if (_viewModel.IsActive)
            {
                Disable_Click(null, null);
                Apply_Click(null, null);
            }
        }

        private void RemoveHdrLut_Click(object sender, RoutedEventArgs e)
        {
            var monitor = _viewModel.SelectedMonitor;
            if (monitor == null) return;
            monitor.HdrLuts.Remove(monitor.HdrLutPath);
            monitor.HdrLutPath = monitor.HdrLuts.FirstOrDefault() ?? "None";
        }

        private void ApplyDarkMode()
        {
            IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
            int useDarkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
        }
    }
}