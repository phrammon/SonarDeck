namespace SonarController
{
    /// <summary>
    /// Guards against a second copy running. Two instances would fight over the
    /// serial port, so the loser bails out instead.
    /// </summary>
    internal sealed class SingleInstance : IDisposable
    {
        // Session-local names: each interactive login gets its own copy, which is
        // what we want for an app that autostarts per user at logon.
        private const string MutexName = @"Local\SonarDeck.SingleInstance";
        private const string SignalName = @"Local\SonarDeck.ShowTray";

        private Mutex? _mutex;
        private EventWaitHandle? _signal;

        /// <summary>
        /// Tries to become the only running instance.
        /// </summary>
        /// <returns>
        /// True if this process should carry on. False if another instance already
        /// owns the app and has been asked to surface its tray icon.
        /// </returns>
        public bool TryAcquire()
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);

            if (isFirst)
            {
                _signal = TryCreateSignal();
                return true;
            }

            // Another instance is already up. Nudge it so the user gets visible
            // feedback rather than an app that silently refuses to start.
            SignalExistingInstance();

            _mutex.Dispose();
            _mutex = null;

            return false;
        }

        /// <summary>
        /// Invoked on a background thread each time another instance tries to
        /// start, so the running one can flash its tray icon. Thread marshalling
        /// back to the UI thread is the caller's problem.
        /// </summary>
        public void WaitForActivationSignal(Action onActivated)
        {
            if (_signal is null)
                return;

            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        if (!_signal.WaitOne())
                            return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return; // shutting down
                    }

                    onActivated();
                }
            })
            {
                IsBackground = true,
                Name = "SonarDeck activation listener"
            };

            thread.Start();
        }

        private static EventWaitHandle? TryCreateSignal()
        {
            try
            {
                // Created here and closed on exit, so it only exists while the
                // owning instance is alive.
                return new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not create the activation signal: {ex.Message}");
                return null;
            }
        }

        private static void SignalExistingInstance()
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(SignalName);
                signal.Set();
                Log.Info("Another instance is already running; asked it to show its tray icon.");
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Log.Warn("Another instance holds the app but has no activation signal yet.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not signal the running instance: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _signal?.Dispose();
            _signal = null;

            _mutex?.Dispose();
            _mutex = null;
        }
    }
}