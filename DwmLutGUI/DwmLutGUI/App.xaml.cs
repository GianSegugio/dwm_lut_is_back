using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DwmLutGUI
{
    
    
    
    public partial class App
    {
        public static KeyboardListener KListener = new KeyboardListener();

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            try
            {
                base.OnStartup(e);
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(
                    "Startup crash:\n\n" + ex.ToString(),
                    "DwmLutGUI Error",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
                Shutdown(1);
            }
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            System.Windows.Forms.MessageBox.Show(
                "Unhandled exception:\n\n" + e.Exception.ToString(),
                "DwmLutGUI Error",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            System.Windows.Forms.MessageBox.Show(
                "Fatal exception:\n\n" + e.ExceptionObject.ToString(),
                "DwmLutGUI Fatal Error",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }

        private void App_OnExit(object sender, ExitEventArgs e)
        {
            KListener.Dispose();
        }
    }
}