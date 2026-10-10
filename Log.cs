using System.Text;

namespace SonarController
{
    /// <summary>
    /// Minimal file logger. The app is a windowless tray application, so the
    /// diagnostics we used to print to a console go to disk instead.
    /// </summary>
    internal static class Log
    {
        private const long MaxBytes = 1_048_576; // 1 MB, then roll over to .1

        private static readonly object Gate = new();

        private static readonly string FilePath = Path.Combine(
            AppContext.BaseDirectory, "sonar-deck.log");

        public static void Info(string message) => Write("INFO", message, null);

        public static void Warn(string message) => Write("WARN", message, null);

        public static void Error(string message, Exception? exception = null) =>
            Write("ERROR", message, exception);

        private static void Write(string level, string message, Exception? exception)
        {
            try
            {
                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" [").Append(level).Append("] ")
                    .Append(message);

                if (exception is not null)
                    line.AppendLine().Append(exception);

                line.AppendLine();

                lock (Gate)
                {
                    Roll();
                    File.AppendAllText(FilePath, line.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never take the app down (e.g. read-only install folder).
            }
        }

        private static void Roll()
        {
            var existing = new FileInfo(FilePath);

            if (!existing.Exists || existing.Length < MaxBytes)
                return;

            File.Move(FilePath, FilePath + ".1", overwrite: true);
        }
    }
}