using Microsoft.Win32;

namespace SonarController
{
    /// <summary>
    /// Manages the per-user "Start with Windows" entry.
    ///
    /// This writes HKCU\...\Run, which starts the app at logon inside the
    /// interactive session — that is what keeps the tray icon on screen. A task
    /// that ran on boot instead would live in session 0, where a tray icon has no
    /// desktop to appear on.
    /// </summary>
    internal static class AutoStart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "SonarDeck";

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);

                return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read the autostart entry: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Adds or removes the entry. Returns false if the registry refused, so
        /// the caller can put the menu checkmark back where it belongs.
        /// </summary>
        public static bool Set(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

                if (key is null)
                {
                    Log.Error("Could not open the Run key for writing.");
                    return false;
                }

                if (!enabled)
                {
                    if (key.GetValue(ValueName) is null)
                        return true; // already off

                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                    Log.Info("Autostart disabled.");
                    return true;
                }

                var executable = Environment.ProcessPath;

                if (string.IsNullOrEmpty(executable))
                {
                    Log.Error("Cannot resolve the executable path for the autostart entry.");
                    return false;
                }

                // The quotes matter: the Run key is parsed like a command line and
                // the path contains spaces.
                key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
                Log.Info($"Autostart enabled: {executable}");

                if (executable.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
                    Log.Warn($"Autostart points at a build output ({executable}); it will launch that build, not an installed copy.");

                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Could not update the autostart entry.", ex);
                return false;
            }
        }
    }
}