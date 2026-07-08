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
        private MonitorData _selectedMonitor;
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
            OnPropertyChanged(nameof(SdrLutPath));
            OnPropertyChanged(nameof(HdrLutPath));
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

        public MonitorData SelectedMonitor
        {
            set
            {
                if (value == _selectedMonitor) return;
                _selectedMonitor = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SdrLutPath));
                OnPropertyChanged(nameof(HdrLutPath));
            }
            get => _selectedMonitor;
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

        public string SdrLutPath
        {
            set
            {
                if (SelectedMonitor == null || SelectedMonitor.SdrLutPath == value) return;
                SelectedMonitor.SdrLutPath = value;
                OnPropertyChanged();

                SaveConfig();
            }
            get => SelectedMonitor?.SdrLutPath;
        }

        public string HdrLutPath
        {
            set
            {
                if (SelectedMonitor == null || SelectedMonitor.HdrLutPath == value) return;
                SelectedMonitor.HdrLutPath = value;
                OnPropertyChanged();

                SaveConfig();
            }
            get => SelectedMonitor?.HdrLutPath;
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
            }
            get => _isActive;
        }

        public bool CanApply { get; }

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
            var selectedPath = SelectedMonitor?.DevicePath;
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

            var paths = WindowsDisplayAPI.DisplayConfig.PathInfo.GetActivePaths();
            var hdrStates = HdrInfo.GetHdrStates();   // device path -> currently in HDR mode
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
                bool isHdr;
                monitor.IsHdr = !string.IsNullOrEmpty(devicePath) && hdrStates.TryGetValue(devicePath, out isHdr) && isHdr;
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

            if (selectedPath == null) return;

            var previous = Monitors.FirstOrDefault(monitor => monitor.DevicePath == selectedPath);
            if (previous != null)
            {
                SelectedMonitor = previous;
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
            UpdateActiveStatus();
        }

        private void UpdateActiveStatus()
        {
            var status = Injector.GetStatus();
            if (status != null)
            {
                IsActive = (bool)status;
                if (status == true)
                {
                    ActiveText = "Active" + (_configChanged ? " (changed)" : "");
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
            var oldState = string.Join(";", Monitors.Select(m => m.Position + "|" + m.SdrLutPath + "|" + m.HdrLutPath));

            UpdateMonitors();

            var newState = string.Join(";", Monitors.Select(m => m.Position + "|" + m.SdrLutPath + "|" + m.HdrLutPath));

            if (oldState == newState)
            {
                return;
            }

            if (!_configChanged)
            {
                ReInject();
            }
        }

        private void DispatcherTimer_Tick(object sender, EventArgs e)
        {
            UpdateActiveStatus();
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}