using small_window2;


namespace small_window2
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);


            // Initialize the application configuration and create the overlay window
            ApplicationConfiguration.Initialize();
            //using var ol = new OverlayWindow();
            var mask = new MaskWindow(null,null);
            //using RoiBorderWindow border = new RoiBorderWindow(out MaskWindow mask);

            mask.Show();

            Application.Run();
            mask.Dispose();
        }
    }
}