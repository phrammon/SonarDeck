using System.Windows.Forms;

namespace SonarController
{
    internal class Program
    {
        // STAThread is required for the tray icon's context menu.
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();

            // Two copies would fight over the serial port.
            using var instance = new SingleInstance();

            if (!instance.TryAcquire())
                return;

            var bridge = new SonarBridge();

            // The context is built before the bridge starts so it cannot miss the
            // very first VolumeTargetsChanged report (which names the device the
            // media keys should drive). Keep Start() below this line.
            using var context = new TrayApplicationContext(bridge);

            instance.WaitForActivationSignal(context.NudgeToFront);

            bridge.Start();

            // Blocks until Exit is chosen from the tray menu.
            Application.Run(context);
        }
    }
}