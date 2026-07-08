using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;

namespace DwmLutGUI
{
    public class MonitorData : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public static event PropertyChangedEventHandler StaticPropertyChanged;

        private string _sdrLutPath;
        private string _hdrLutPath;

        public MonitorData(string devicePath, uint sourceId, string name, string connector, string position,
            string sdrLutPath, string hdrLutPath)
        {
            SdrLuts = new ObservableCollection<string>();
            HdrLuts = new ObservableCollection<string>();
            // Never store null identity fields: they later feed XAttribute(name,value) in SaveConfig,
            // which throws ArgumentNullException("value") on null. Coalesce at the source.
            DevicePath = devicePath ?? "";
            SourceId = sourceId;
            Name = string.IsNullOrEmpty(name) ? "???" : name;
            Connector = connector ?? "";
            Position = position ?? "";
            SdrLutPath = sdrLutPath;
            HdrLutPath = hdrLutPath;
        }

        // Config-only monitors (present in config.xml but not currently connected).
        public MonitorData(string devicePath, string sdrLutPath, string hdrLutPath)
        {
            SdrLuts = new ObservableCollection<string>();
            HdrLuts = new ObservableCollection<string>();
            DevicePath = devicePath ?? "";
            Name = "???";        // was previously left null -> ArgumentNullException on next SaveConfig
            Connector = "";
            Position = "";
            SdrLutPath = sdrLutPath;
            HdrLutPath = hdrLutPath;
        }

        // 1-based, globally-unique ordinal for the '#' column. Assigned during enumeration.
        // Fixes the "1, 1, 2" overlap: DisplaySource.SourceId is per-ADAPTER and collides across GPUs.
        public int DisplayIndex { get; set; }

        public string DevicePath { get; }
        public uint SourceId { get; }
        public string Name { get; }
        public string Connector { get; }
        public string Position { get; }
        public bool IsHdr { get; set; }                    // display currently in HDR (advanced color) mode
        public string HdrStatus => IsHdr ? "HDR" : "SDR";  // shown in the monitor list "Mode" column

        // Live per-monitor state for the "Status" column, pushed by the composition-blocker watcher:
        // "Inactive" / "Active, windowed mode" / "Active, fullscreen mode".
        private string _status = "Inactive";
        public string Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            }
        }

        public ObservableCollection<string> SdrLuts { get; set; }
        public ObservableCollection<string> HdrLuts { get; set; }

        public string SdrLutPath
        {
            set
            {
                if (value == _sdrLutPath) return;
                if (value == null) return;
                if (value != "None" && !SdrLuts.Contains(value))
                    SdrLuts.Add(value);
                _sdrLutPath = value != "None" ? value : null;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SdrLutPath)));       // update the LUT ComboBox selection
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SdrLutFilename)));
                StaticPropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SdrLutFilename)));
            }
            get => _sdrLutPath;
        }

        public string HdrLutPath
        {
            set
            {
                if (value == _hdrLutPath) return;
                if (value == null) return;                       // was missing -> could Add(null) to HdrLuts
                if (value != "None" && !HdrLuts.Contains(value))
                    HdrLuts.Add(value);
                _hdrLutPath = value != "None" ? value : null;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HdrLutPath)));       // update the LUT ComboBox selection
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HdrLutFilename)));
                StaticPropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HdrLutFilename)));
            }
            get => _hdrLutPath;
        }

        public string SdrLutFilename =>
            string.IsNullOrEmpty(SdrLutPath) ? "None" : (Path.GetFileName(SdrLutPath) ?? "None");

        public string HdrLutFilename =>
            string.IsNullOrEmpty(HdrLutPath) ? "None" : (Path.GetFileName(HdrLutPath) ?? "None");
    }
}