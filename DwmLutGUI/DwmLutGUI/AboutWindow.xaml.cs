using System.Windows;
using System.Windows.Navigation;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System;

namespace DwmLutGUI
{
    public partial class AboutWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public AboutWindow()
        {
            InitializeComponent();
            ApplyDarkMode();
        }

        private void ApplyDarkMode()
        {
            IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
            int useDarkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
        }

        private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            var processStartInfo = new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(processStartInfo);
        }
    }
}