using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Xml;
using System.Xml.Linq;

namespace DwmLutGUI
{
    internal class MainViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private string _activeText;
        private bool _isActive;
        private Key _toggleKey;
        private bool _autostartAsked;

        private readonly string _configPath;

        private bool _configChanged;
        private XElement _lastConfig;
        private XElement _activeConfig;

        public MainViewModel()
        {
            UpdateActiveStatus();
            var dispatcherTimer = new System.Windows.Threading.DispatcherTimer();
            dispatcherTimer.Tick += DispatcherTimer_Tick;
            dispatcherTimer.Interval = new TimeSpan(0, 0, 1);
            dispatcherTimer.Start();

            _configPath = AppDomain.CurrentDomain.BaseDirectory + "config.xml";

            _allMonitors = new List<MonitorData>();
            Monitors = new ObservableCollection<MonitorData>();
            UpdateMonitors();

            CanApply = !Injector.NoDebug;
            MonitorData.StaticPropertyChanged += MonitorDataOnStaticPropertyChanged;
        }

        private void MonitorDataOnStaticPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Each monitor column binds straight to its own MonitorData, so there is nothing to
            // re-notify here - this exists purely to persist the change.
            if (!_updatingMonitors) SaveConfig();   // never persist a half-rebuilt list (see flag)
        }

        public string ActiveText
        {
            private set
            {
                if (value == _activeText) return;
                _activeText = value;
                OnPropertyChanged();
            }
            get => _activeText;
        }

        private void UpdateConfigChanged()
        {
            _configChanged = _lastConfig != _activeConfig && !XNode.DeepEquals(_lastConfig, _activeConfig);
        }

        // A LUT path is "usable" if it is empty/None (no LUT) or the file actually exists. Files that
        // are gone (deleted, or on a disconnected drive whose letter no longer resolves) are treated as
        // missing so they can be pruned rather than lingering in the list or being applied.
        private static bool LutFileOk(string path) =>
            string.IsNullOrEmpty(path) || path == "None" || File.Exists(path);

        private static System.Collections.Generic.List<string> PruneMissing(System.Collections.Generic.List<string> paths) =>
            paths?.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).ToList();

        private void SaveConfig()
        {
            if (_allMonitors.Count == 0)
                return;

            try
            {
                var xElem = new XElement("monitors",
                    new XAttribute("lut_toggle", _toggleKey),
                    new XAttribute("autostart_asked", _autostartAsked),
                    _allMonitors
                        // an entry with no usable identity can never be matched back and would only
                        // serve to poison the writer; drop it defensively.
                        .Where(x => !string.IsNullOrEmpty(x.DevicePath) || !string.IsNullOrEmpty(x.Name))
                        .Select(x =>
                            new XElement("monitor",
                                // Coalesce EVERY value: XAttribute(name, null) throws
                                // "ArgumentNullException: Value cannot be null. Parameter name: value".
                                new XAttribute("path", x.DevicePath ?? ""),
                                new XAttribute("name", x.Name ?? "???"),
                                x.SdrLutPath != null ? new XAttribute("sdr_lut", x.SdrLutPath) : null,
                                x.HdrLutPath != null ? new XAttribute("hdr_lut", x.HdrLutPath) : null,
                                x.SdrLuts != null
                                    ? new XElement("sdr_luts",
                                        x.SdrLuts.Where(s => !string.IsNullOrEmpty(s))
                                                 .Select(s => new XElement("sdr_lut", s)))
                                    : null,
                                x.HdrLuts != null
                                    ? new XElement("hdr_luts",
                                        x.HdrLuts.Where(s => !string.IsNullOrEmpty(s))
                                                 .Select(s => new XElement("hdr_lut", s)))
                                    : null)));

                xElem.Save(_configPath);

                _lastConfig = xElem;
                UpdateConfigChanged();
                UpdateActiveStatus();
            }
            catch (Exception)
            {
                // Persisting config must never take down the UI. A failed save is non-fatal;
                // the in-memory selection already applied.
            }
        }

        public Key ToggleKey
        {
            set
            {
                if (value == _toggleKey) return;
                _toggleKey = value;
                OnPropertyChanged();
                SaveConfig();
            }
            get => _toggleKey;
        }

        private bool _autostartEnabled;

        /// <summary>
        /// Whether the autostart scheduled task currently exists. Set from the real task state rather
        /// than remembered, so the control still tells the truth if the task is removed elsewhere
        /// (Task Scheduler, an uninstall, a different machine profile).
        /// </summary>
        public bool AutostartEnabled
        {
            get => _autostartEnabled;
            set
            {
                if (_autostartEnabled == value) return;
                _autostartEnabled = value;
                OnPropertyChanged(nameof(AutostartEnabled));
                OnPropertyChanged(nameof(AutostartLabel));
                OnPropertyChanged(nameof(AutostartButtonText));
            }
        }

        public string AutostartLabel => _autostartEnabled ? "Autostart (enabled):" : "Autostart (disabled):";

        public string AutostartButtonText => _autostartEnabled ? "Turn off" : "Turn on";

        public bool AutostartAsked
        {
            set
            {
                if (value == _autostartAsked) return;
                _autostartAsked = value;
                OnPropertyChanged();
                SaveConfig();
            }
            get => _autostartAsked;
        }

        public bool IsActive
        {
            set
            {
                if (value == _isActive) return;
                _isActive = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanDisableLut));
            }
            get => _isActive;
        }

        /// <summary>
        /// Static capability: this process holds the privilege needed to inject and patch at all.
        /// Deliberately NOT "the Apply button should be clickable right now" - the gamma controls
        /// depend on this too, and folding button state into it would silently gate them as well.
        /// </summary>
        public bool CanApply { get; }

        private bool _autostartOpInProgress;

        /// <summary>
        /// True while a gamma toggle or an autostart change is running. Every one of these operations
        /// is synchronous on the UI thread, so they can never truly overlap - but a gamma toggle
        /// freezes the UI for seconds, and any click landing in that window is delivered afterwards.
        /// Gating on this makes those clicks land on a disabled control and be discarded instead.
        /// </summary>
        public bool IsBusy => _gammaOpInProgress || _autostartOpInProgress;

        /// <summary>
        /// Apply stays available while a LUT is active: it doubles as "re-apply", which is what the
        /// "Active (changed)" status prompts after a LUT is picked from a dropdown.
        /// </summary>
        public bool CanApplyLut => CanApply && !IsBusy;

        public bool CanDisableLut => IsActive && !IsBusy;

        public bool CanToggleAutostart => !IsBusy;

        /// <summary>Set around an autostart change so the other controls gate on it.</summary>
        public bool AutostartOpInProgress
        {
            get => _autostartOpInProgress;
            set
            {
                if (_autostartOpInProgress == value) return;
                _autostartOpInProgress = value;
                RaiseBusyDependents();
            }
        }

        private void RaiseBusyDependents()
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanApplyLut));
            OnPropertyChanged(nameof(CanDisableLut));
            OnPropertyChanged(nameof(CanToggleAutostart));
            OnPropertyChanged(nameof(CanEnableGammaFix));
            OnPropertyChanged(nameof(CanDisableGammaFix));
        }

        /// <summary>
        /// SDR-in-HDR gamma fix: patches DWM's SDR-to-scRGB shaders to a pure 2.2 curve.
        /// Independent of the LUT - this corrects how SDR content is mapped into the HDR space,
        /// the LUT corrects the display. Native HDR content is unaffected.
        /// </summary>
        public bool GammaFixEnabled
        {
            get => Injector.GammaFixEnabled;
            private set
            {
                if (Injector.GammaFixEnabled == value) return;
                Injector.GammaFixEnabled = value;
                OnPropertyChanged(nameof(GammaFixEnabled));
            }
        }

        private bool _gammaFixActive;

        // Set while we are deliberately restarting DWM, so the resulting display-settings events
        // are ignored instead of triggering a spurious re-inject.
        private bool _suppressDisplayEvents;

        /// <summary>
        /// Whether the patch is actually live in DWM right now. Read back out of DWM's memory
        /// rather than remembered, so it stays correct across GUI restarts.
        /// </summary>
        public bool GammaFixActive
        {
            get => _gammaFixActive;
            private set
            {
                if (_gammaFixActive == value) return;
                _gammaFixActive = value;
                OnPropertyChanged(nameof(GammaFixActive));
                OnPropertyChanged(nameof(CanEnableGammaFix));
                OnPropertyChanged(nameof(CanDisableGammaFix));
            }
        }

        // True while a gamma operation is running, so a second click cannot start another one.
        private bool _gammaOpInProgress;

        /// <summary>
        /// True while we are still inside the minimum interval between DWM restarts. The buttons
        /// are disabled during this window rather than blocking the UI thread, so repeat clicks
        /// cannot queue up into a burst of restarts.
        /// </summary>
        public bool GammaFixCoolingDown => Injector.RestartCooldownRemainingMs > 0;

        /// <summary>Gamma values offered by the dropdown next to the On/Off buttons.</summary>
        public double[] GammaFixValues { get; } = { 2.2, 2.4, 2.6 };

        /// <summary>
        /// Target gamma for the fix. Changing this while the fix is already live deliberately does
        /// nothing to the running DWM: the patch is baked into shaders that already exist, so a new
        /// value only takes effect on the next off/on cycle.
        /// </summary>
        public double SelectedGammaValue
        {
            get => Injector.GammaFixValue;
            set
            {
                if (Injector.GammaFixValue == value) return;
                Injector.GammaFixValue = value;
                OnPropertyChanged(nameof(SelectedGammaValue));
                GuiDiag.Log("gamma target set to " + value.ToString("0.0") +
                            (GammaFixActive ? " (fix is live - off/on required for it to take effect)" : ""));
            }
        }

        /// <summary>"On" is available only when the fix isn't already live and we are ready.</summary>
        public bool CanEnableGammaFix =>
            CanApply && !_gammaFixActive && !IsBusy && !GammaFixCoolingDown;

        /// <summary>"Off" mirrors it: only when the fix IS live and we are ready.</summary>
        public bool CanDisableGammaFix =>
            _gammaFixActive && !IsBusy && !GammaFixCoolingDown;

        /// <summary>Row label, with a note while the buttons are held back so the wait is explained.</summary>
        public string GammaFixLabel
        {
            get
            {
                const string baseText = "scRGB piecewise -> scRGB 2.x (HDR gamma fix):";
                var left = Injector.RestartCooldownRemainingMs;
                return left > 0
                    ? baseText + "  (settling " + ((left + 999) / 1000) + "s)"
                    : baseText;
            }
        }

        // Last values actually published to the bindings. These are compared against the CURRENT
        // values on every tick, so the gating is level-triggered and self-correcting.
        //
        // It used to be edge-triggered on a "was cooling down" flag, which could desync: a long
        // operation blocks the UI thread, DispatcherTimer coalesces the missed ticks, and the single
        // tick that follows can land after the cooldown has already expired. The transition was then
        // never observed, no PropertyChanged was raised, and the buttons stayed disabled forever
        // with the label frozen mid-countdown. Comparing real values cannot get stuck that way: even
        // if a tick is missed entirely, the next one still sees the mismatch and republishes.
        private string _lastGammaLabel;
        private bool? _lastCanEnableGamma;
        private bool? _lastCanDisableGamma;

        /// <summary>Called from the status timer so the buttons re-enable when the cooldown ends.</summary>
        private void RefreshGammaGating()
        {
            var label = GammaFixLabel;
            if (label != _lastGammaLabel)
            {
                _lastGammaLabel = label;
                OnPropertyChanged(nameof(GammaFixLabel));
            }

            var canEnable = CanEnableGammaFix;
            if (_lastCanEnableGamma != canEnable)
            {
                _lastCanEnableGamma = canEnable;
                OnPropertyChanged(nameof(CanEnableGammaFix));
                OnPropertyChanged(nameof(GammaFixCoolingDown));
            }

            var canDisable = CanDisableGammaFix;
            if (_lastCanDisableGamma != canDisable)
            {
                _lastCanDisableGamma = canDisable;
                OnPropertyChanged(nameof(CanDisableGammaFix));
            }
        }

        private List<MonitorData> _allMonitors { get; }
        // True only while UpdateMonitors is rebuilding the monitor list. During the rebuild,
        // MonitorData constructors set SdrLutPath/HdrLutPath through their setters, which raise
        // StaticPropertyChanged -> SaveConfig; if that ran now it would persist a half-built
        // _allMonitors and drop whichever monitor has not been added yet. Suppress saves here.
        private bool _updatingMonitors;
        public ObservableCollection<MonitorData> Monitors { get; }

        public void UpdateMonitors()
        {
            _updatingMonitors = true;
            try { UpdateMonitorsCore(); }
            finally { _updatingMonitors = false; }
        }

        private void UpdateMonitorsCore()
        {
            // Query the display topology BEFORE clearing anything. QueryDisplayConfig legitimately
            // fails with ERROR_NOT_SUPPORTED while the topology is in flux - which is exactly the
            // state a DWM restart, a monitor hot-plug or a mode change puts it in - so retry
            // briefly, and if it still fails leave the existing monitor list untouched rather than
            // wiping it. (Clearing first and throwing here left the GUI with no monitors at all.)
            IEnumerable<WindowsDisplayAPI.DisplayConfig.PathInfo> paths = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    paths = WindowsDisplayAPI.DisplayConfig.PathInfo.GetActivePaths();
                    break;
                }
                catch (Exception ex)
                {
                    if (attempt == 0) GuiDiag.Log("[monitors] GetActivePaths failed (" + ex.GetType().Name +
                                                  ": " + ex.Message + ") - retrying");
                    System.Threading.Thread.Sleep(150);
                }
            }

            if (paths == null)
            {
                // Topology never settled. Keep the previous state and try again on the next event.
                GuiDiag.Log("[monitors] GetActivePaths never succeeded - keeping previous monitor list");
                return;
            }

            _allMonitors.Clear();
            Monitors.Clear();
            List<XElement> config = null;
            if (File.Exists(_configPath))
            {
                try
                {
                    config = XElement.Load(_configPath).Descendants("monitor").ToList();
                    _toggleKey = (Key)Enum.Parse(typeof(Key), (string)XElement.Load(_configPath).Attribute("lut_toggle"));
                    _autostartAsked = (bool?)XElement.Load(_configPath).Attribute("autostart_asked") ?? false;
                }
                catch
                {
                    _toggleKey = Key.Pause;
                    _autostartAsked = false;
                }
            }
            else
            {
                _toggleKey = Key.Pause;
                _autostartAsked = false;
            }

            // Globally-unique 1-based ordinal. This is the identity used purely for the '#' column;
            // it is independent of DisplaySource.SourceId (which is per-adapter and collides on
            // hybrid multi-GPU laptops, producing "1, 1, 2").
            var displayIndex = 0;

            var colorModes = HdrInfo.GetColorModes(); // device path -> SDR / WCG / HDR
            foreach (var path in paths)
            {
                if (path.IsCloneMember) continue;
                var targetInfo = path.TargetsInfo[0];
                var deviceId = targetInfo.DisplayTarget.TargetId;
                var devicePath = targetInfo.DisplayTarget.DevicePath;
                if (string.IsNullOrEmpty(devicePath))
                {
                    // Some hybrid/virtual adapters expose no device path. Synthesize a stable, unique key
                    // from position + target id so identity/tracking still works and the config writer
                    // never sees a null path.
                    devicePath = "SYNTH\\" + path.Position.X + "_" + path.Position.Y + "_" + deviceId;
                }

                var name = targetInfo.DisplayTarget.FriendlyName;
                if (string.IsNullOrEmpty(name))
                {
                    // Internal laptop panels frequently ship an EDID with no product-name
                    // descriptor, so the display API returns an empty FriendlyName. Label such a
                    // panel by its connection type ("Internal Display") rather than a bare "???".
                    // (This mirrors what Windows Settings shows when a display has no EDID name;
                    // eDP laptop panels report as "Internal" or "...Embedded".)
                    var tech = targetInfo.OutputTechnology.ToString();
                    name = (tech.IndexOf("Internal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            tech.IndexOf("Embedded", StringComparison.OrdinalIgnoreCase) >= 0)
                        ? "Internal Display"
                        : "???";
                }

                var connector = targetInfo.OutputTechnology.ToString();
                if (connector == "DisplayPortExternal")
                {
                    connector = "DisplayPort";
                }

                var position = path.Position.X + "," + path.Position.Y;

                string sdrLutPath = null;
                string hdrLutPath = null;

                var settings = config?.FirstOrDefault(x => (uint?)x.Attribute("id") == deviceId) ??
                               config?.FirstOrDefault(x => (string)x.Attribute("path") == devicePath) ??
                               config?.FirstOrDefault(x => (string)x.Attribute("name") == name);

                if (settings != null)
                {
                    sdrLutPath = (string)settings.Attribute("sdr_lut");
                    hdrLutPath = (string)settings.Attribute("hdr_lut");
                }
                var sdrLutPaths = settings?.Element("sdr_luts")?.Elements("sdr_lut").Select(x => (string)x).ToList();
                var hdrLutPaths = settings?.Element("hdr_luts")?.Elements("hdr_lut").Select(x => (string)x).ToList();
                // Prune LUTs whose file no longer exists on disk, and clear a current selection that is missing.
                sdrLutPaths = PruneMissing(sdrLutPaths);
                hdrLutPaths = PruneMissing(hdrLutPaths);
                if (!LutFileOk(sdrLutPath)) sdrLutPath = null;
                if (!LutFileOk(hdrLutPath)) hdrLutPath = null;
                var monitor = new MonitorData(devicePath, path.DisplaySource.SourceId + 1, name, connector, position,
                    sdrLutPath, hdrLutPath)
                {
                    DisplayIndex = ++displayIndex
                };
                if (sdrLutPaths != null) monitor.SdrLuts = new ObservableCollection<string>(sdrLutPaths);
                if (hdrLutPaths != null) monitor.HdrLuts = new ObservableCollection<string>(hdrLutPaths);
                AdvancedColorMode mode;
                if (string.IsNullOrEmpty(devicePath) || !colorModes.TryGetValue(devicePath, out mode))
                    mode = AdvancedColorMode.Sdr;
                monitor.ColorMode = mode;
                // Advanced colour of either kind means FP16 composition, which is what the injector
                // keys the HDR LUT slot off - so WCG counts as "HDR" for LUT purposes.
                monitor.IsHdr = mode != AdvancedColorMode.Sdr;
                _allMonitors.Add(monitor);
                Monitors.Add(monitor);
            }

            if (config != null)
            {
                foreach (var monitor in config)
                {
                    var path = (string)monitor.Attribute("path");
                    if (string.IsNullOrEmpty(path) || Monitors.Any(x => x.DevicePath == path)) continue;

                    var sdrLutPath = (string)monitor.Attribute("sdr_lut");
                    var hdrLutPath = (string)monitor.Attribute("hdr_lut");

                    var sdrLutPaths = monitor.Element("sdr_luts")?.Elements("sdr_lut").Select(x => (string)x).ToList();
                    var hdrLutPaths = monitor.Element("hdr_luts")?.Elements("hdr_lut").Select(x => (string)x).ToList();
                    sdrLutPaths = PruneMissing(sdrLutPaths);
                    hdrLutPaths = PruneMissing(hdrLutPaths);
                    if (!LutFileOk(sdrLutPath)) sdrLutPath = null;
                    if (!LutFileOk(hdrLutPath)) hdrLutPath = null;
                    var newMonitorData = new MonitorData(path, sdrLutPath, hdrLutPath) { DisplayIndex = 0 };
                    if (sdrLutPaths != null) newMonitorData.SdrLuts = new ObservableCollection<string>(sdrLutPaths);
                    if (hdrLutPaths != null) newMonitorData.HdrLuts = new ObservableCollection<string>(hdrLutPaths);
                    _allMonitors.Add(newMonitorData);
                }
            }

        }

        public void ReInject()
        {
            Injector.Uninject();
            if (!Monitors.All(monitor =>
                    string.IsNullOrEmpty(monitor.SdrLutPath) && string.IsNullOrEmpty(monitor.HdrLutPath)))
            {
                Injector.Inject(Monitors);
            }

            _activeConfig = _lastConfig;
            UpdateConfigChanged();

            UpdateActiveStatus();
        }

        public void Uninject()
        {
            Injector.Uninject();
            UpdateActiveStatus(true);
        }

        /// <summary>Turn the gamma fix on. Leaves the LUT exactly as it was.</summary>
        public void EnableGammaFix()
        {
            if (IsBusy || GammaFixCoolingDown)
            {
                GuiDiag.Log("gamma ON ignored (operation in progress or cooling down)");
                return;
            }

            var lutWasActive = IsActive;
            GammaFixEnabled = true;
            ApplyGammaFixChange(lutWasActive);
        }

        /// <summary>Turn the gamma fix off. Leaves the LUT exactly as it was.</summary>
        public void DisableGammaFix()
        {
            if (IsBusy || GammaFixCoolingDown)
            {
                GuiDiag.Log("gamma OFF ignored (operation in progress or cooling down)");
                return;
            }

            var lutWasActive = IsActive;
            GammaFixEnabled = false;
            ApplyGammaFixChange(lutWasActive);
        }

        private void ApplyGammaFixChange(bool keepLutActive)
        {
            var monitorsBefore = Monitors.Count;
            GuiDiag.Log("=== gamma fix -> " + (Injector.GammaFixEnabled ? "ON" : "OFF") +
                        " (keepLutActive=" + keepLutActive + ", monitors=" + monitorsBefore + ") ===");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            _gammaOpInProgress = true;
            RaiseBusyDependents();

            _suppressDisplayEvents = true;
            try
            {
                ApplyGammaFixChangeCore(keepLutActive);
            }
            catch (Exception ex)
            {
                GuiDiag.LogError("ApplyGammaFixChange", ex);
                throw;
            }
            finally
            {
                _suppressDisplayEvents = false;
                _gammaOpInProgress = false;
                RaiseBusyDependents();
                // Invalidate the published values so the next tick definitely republishes.
                _lastGammaLabel = null;
                _lastCanEnableGamma = null;
                _lastCanDisableGamma = null;
                OnPropertyChanged(nameof(CanEnableGammaFix));
                OnPropertyChanged(nameof(CanDisableGammaFix));
                OnPropertyChanged(nameof(GammaFixLabel));
                GuiDiag.Log("=== gamma fix sequence done in " + sw.ElapsedMilliseconds + "ms ===");
            }

            // Re-read the topology once things have settled, so the monitor list reflects reality
            // after the restart even though the events during it were ignored.
            UpdateMonitors();
            UpdateActiveStatus(true);
            GuiDiag.Log("post-settle: monitors=" + Monitors.Count + " lutActive=" + IsActive +
                        " gammaActive=" + GammaFixActive);

            if (Monitors.Count < monitorsBefore)
            {
                // The display pipeline did not come back intact. Restarting DWM again now would
                // very likely make it worse, so this is surfaced rather than silently retried.
                GuiDiag.Log("*** DISPLAY LOST: monitor count fell from " + monitorsBefore + " to " +
                            Monitors.Count + " across the DWM restart. Avoid further gamma toggles " +
                            "until the display configuration has recovered (reconnect the display or reboot).");
            }
        }

        private void ApplyGammaFixChangeCore(bool keepLutActive)
        {
            Injector.Uninject();
            GuiDiag.Log("  uninjected");

            // Stage first: none of the file/ACL work depends on DWM, and doing it after the
            // restart would put it inside the window where DWM is creating its shaders. Staging
            // first also means a staging failure leaves DWM untouched instead of already killed.
            if (keepLutActive)
            {
                Injector.StageForInject(Monitors);
                GuiDiag.Log("  staged LUTs for re-inject");
            }

            // The patch has to land before DWM builds its pixel shaders, so a state change needs
            // a fresh DWM either way: to switch the fix on, and equally to switch it off (the
            // already-created patched shaders can only be discarded by rebuilding them).
            Injector.RestartDwmAndWait();

            if (Injector.GammaFixEnabled)
            {
                // Patched from here, with DWM suspended - no race against shader creation.
                var sites = Injector.ApplyEotfPatch(Injector.GammaFixValue);
                if (sites == 0)
                {
                    GuiDiag.Log("  WARNING: patch reported 0 sites - the fix is probably NOT active");
                }
            }

            if (keepLutActive)
            {
                Injector.InjectStaged();
                GuiDiag.Log("  re-injected LUT DLL");
                _activeConfig = _lastConfig;
                UpdateConfigChanged();
            }

            // Re-reads the real state out of DWM, so a patch that silently failed shows up as
            // the fix being off rather than the button lying.
            UpdateActiveStatus();
        }

        private void UpdateActiveStatus(bool forceGammaCheck = false)
        {
            // Ground truth from DWM's memory, same idea as GetStatus() for the LUT DLL. The check
            // reads DWM's mapped dwmcore image, so the 1s status timer takes a cached answer and
            // only real state changes force a fresh read.
            var gamma = Injector.GetGammaFixStatus(forceGammaCheck);
            if (gamma != null)
            {
                GammaFixActive = (bool)gamma;
                Injector.GammaFixEnabled = (bool)gamma;   // keep the intent in sync with reality
                OnPropertyChanged(nameof(GammaFixEnabled));
            }

            var status = Injector.GetStatus();
            if (status != null)
            {
                // IsActive stays LUT-specific: it gates the Disable button, which unloads the LUT and
                // cannot turn the gamma fix off (that needs a DWM restart via the gamma Off button).
                IsActive = (bool)status;

                // The status line is the broader "is this tool doing anything" indicator, so either
                // correction counts. The "(changed)" hint stays tied to the LUT, since it means
                // "press Apply to re-apply" and would be meaningless with no LUT applied.
                var lutActive = status == true;
                if (lutActive || GammaFixActive)
                {
                    ActiveText = "Active" + (lutActive && _configChanged ? " (changed)" : "");
                }
                else
                {
                    ActiveText = "Inactive";
                }
            }
            else
            {
                IsActive = false;
                ActiveText = "???";
            }
        }

        public void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            // Killing DWM makes Windows fire display-settings changes. Those are our own doing, and
            // acting on them mid-operation would re-enter injection while the topology is still
            // settling, so they are ignored until the operation completes.
            if (_suppressDisplayEvents) return;

            // Captured before the refresh: connecting a display must not turn the LUT on by itself.
            // ReInject() injects whenever any monitor has a LUT assigned, so without this a plugged-in
            // monitor would silently apply LUTs the user had deliberately disabled.
            var wasActive = IsActive;

            var oldState = string.Join(";", Monitors.Select(m => m.Position + "|" + m.SdrLutPath + "|" + m.HdrLutPath));

            UpdateMonitors();

            var newState = string.Join(";", Monitors.Select(m => m.Position + "|" + m.SdrLutPath + "|" + m.HdrLutPath));

            if (oldState == newState)
            {
                return;
            }

            // Re-apply only if the LUT was already applied: the display layout changed, so the staged
            // .cube files are named for the old positions and need restaging. If it was off, leave it
            // off - the new monitor list is already refreshed above.
            if (wasActive && !_configChanged)
            {
                ReInject();
            }
        }

        private void DispatcherTimer_Tick(object sender, EventArgs e)
        {
            // The status probe touches other processes and can throw transiently. It must never
            // prevent the gating refresh, because that is what re-enables the gamma buttons after
            // the cooldown - a swallowed failure there would leave them disabled indefinitely.
            try
            {
                UpdateActiveStatus();
            }
            catch (Exception ex)
            {
                GuiDiag.LogError("DispatcherTimer_Tick/UpdateActiveStatus", ex);
            }

            try
            {
                RefreshGammaGating();
            }
            catch (Exception ex)
            {
                GuiDiag.LogError("DispatcherTimer_Tick/RefreshGammaGating", ex);
            }
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}