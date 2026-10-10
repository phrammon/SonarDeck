using System.Drawing;
using System.Windows.Forms;

namespace SonarController
{
    /// <summary>
    /// Keeps the app alive in the background with no main window: the tray icon
    /// reports the current link state through its tooltip and the top menu item,
    /// and offers Restart connection / Exit.
    /// </summary>
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private const int MaxToolTipLength = 63;
        private const string AppName = "SonarDeck";
        private const string IconResourceName = "SonarDeck.ico";
        private const string AlreadyRunningTitle = "SonarDeck is already running";

        private const int BalloonTipTimeout = 5000;

        private static readonly Icon AppIcon = LoadIcon();

        private readonly SonarBridge _bridge;
        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _autoStartItem;
        private readonly ToolStripMenuItem _volumeKeysItem;
        private readonly System.Windows.Forms.Timer _statusTimer;

        // The device lists are rebuilt every time the menu opens, since devices
        // come and go and the redirections can also be changed from Sonar itself.
        // They sit between these two separators, which fence them off from the
        // status line above and the settings below.
        private readonly ToolStripSeparator _deviceSeparatorTop = new();
        private readonly ToolStripSeparator _deviceSeparatorBottom = new();
        private readonly List<ToolStripItem> _deviceItems = [];

        private readonly Control _dispatch = new() { Visible = false };

        // Created on the UI thread on purpose: a low-level keyboard hook is only
        // delivered to a thread that pumps messages, and only the WinForms loop
        // here does.
        private readonly AudioDeviceVolume _volume = new();
        private MediaKeyHook? _mediaKeyHook;
        private VolumeOverlay? _overlay;
        private bool _volumeKeysWanted = true;

        private BridgeStatus _lastStatus = (BridgeStatus)(-1);
        private string _lastDetail = "";
        private bool _updatingAutoStart;
        private bool _cleanedUp;

        public TrayApplicationContext(SonarBridge bridge)
        {
            _bridge = bridge;

            // Reading Handle forces the window into existence now, while still on
            // the UI thread. CreateControl() will not do it for a parentless
            // control, and BeginInvoke refuses to run without one.
            _ = _dispatch.Handle;

            _statusItem = new ToolStripMenuItem("Starting...") { Enabled = false };

            // Checked is seeded from the registry before the handler is attached,
            // so simply opening the menu never rewrites the entry.
            _autoStartItem = new ToolStripMenuItem("Start with Windows")
            {
                CheckOnClick = true,
                Checked = AutoStart.IsEnabled()
            };
            _autoStartItem.CheckedChanged += OnAutoStartToggled;

            _volumeKeysItem = new ToolStripMenuItem("Route volume keys to speakers")
            {
                CheckOnClick = true,
                Checked = true
            };
            _volumeKeysItem.CheckedChanged += OnVolumeKeysToggled;

            var restartItem = new ToolStripMenuItem(
                "Restart connection", null, (_, _) => _bridge.Restart());

            var exitItem = new ToolStripMenuItem(
                "Exit", null, (_, _) => ExitThread());

            _menu = new ContextMenuStrip();
            _menu.Items.Add(_statusItem);
            _menu.Items.Add(_deviceSeparatorTop);
            _menu.Items.Add(_deviceSeparatorBottom);
            _menu.Items.Add(_autoStartItem);
            _menu.Items.Add(_volumeKeysItem);
            _menu.Items.Add(restartItem);
            _menu.Items.Add(exitItem);

            // Rebuilt on every open rather than once, so a device plugged in or a
            // redirection changed in Sonar shows up without restarting the app.
            _menu.Opening += (_, _) => PopulateDeviceMenus();

            _notifyIcon = new NotifyIcon
            {
                Icon = AppIcon,
                Text = AppName,
                Visible = true,
                ContextMenuStrip = _menu
            };

            // The bridge reports from a worker thread while NotifyIcon is
            // UI-thread only, so poll for state changes rather than marshalling.
            _statusTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _statusTimer.Tick += (_, _) => RefreshStatus();
            _statusTimer.Start();

            var (initialStatus, initialDetail) = _bridge.GetStatus();
            ApplyStatus(initialStatus, initialDetail);

            // The hook is armed straight away, but stays inert until the bridge
            // reports which device to drive and only if the user has it enabled.
            InstallMediaKeyHook();

            _volume.Changed += OnVolumeChanged;
            _bridge.VolumeTargetsChanged += OnVolumeTargetsChanged;
        }

        /// <summary>
        /// Shows the on-screen indicator. Fires straight from the keyboard hook,
        /// which already runs on this thread, so no marshalling is needed.
        /// </summary>
        private void OnVolumeChanged(AudioDeviceVolume.State state)
        {
            try
            {
                _overlay ??= new VolumeOverlay();
                _overlay.Display(state.Level, state.Muted, state.Device);
            }
            catch (Exception ex)
            {
                // The indicator is cosmetic; never let it break volume control.
                Log.Warn($"Could not show the volume indicator: {ex.Message}");
            }
        }

        private void OnVolumeTargetsChanged(string[] deviceIds)
        {
            // Raised on the bridge worker; the hook and volume control are
            // UI-thread objects, so hop across before touching them.
            try
            {
                _dispatch.BeginInvoke(() => _volume.SetTargets(deviceIds));
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not apply the new volume key target: {ex.Message}");
            }
        }

        private void InstallMediaKeyHook()
        {
            if (!_volumeKeysWanted || _mediaKeyHook is not null || _cleanedUp)
                return;

            try
            {
                _mediaKeyHook = new MediaKeyHook(_volume);
            }
            catch (Exception ex)
            {
                Log.Error("Could not install the media key hook; volume keys left to Windows.", ex);
            }
        }

        private void RemoveMediaKeyHook()
        {
            _mediaKeyHook?.Dispose();
            _mediaKeyHook = null;
        }

        private void OnVolumeKeysToggled(object? sender, EventArgs e)
        {
            _volumeKeysWanted = _volumeKeysItem.Checked;

            if (_volumeKeysWanted)
                InstallMediaKeyHook();
            else
                RemoveMediaKeyHook();

            Log.Info($"Volume key routing {( _volumeKeysWanted ? "enabled" : "disabled")}.");
        }

        /// <summary>
        /// Rebuilds the device lists: physical inputs first, then a rule, then
        /// physical outputs. The currently redirected device in each group is the
        /// one shown checked.
        /// </summary>
        private void PopulateDeviceMenus()
        {
            ClearDeviceItems();

            IReadOnlyList<SonarDevice> devices;
            SonarRedirections current;

            try
            {
                (devices, current) = _bridge.GetDeviceState();
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read the Sonar device list for the tray menu: {ex.Message}");
                AddDeviceItem(new ToolStripMenuItem("Sonar not available") { Enabled = false });
                return;
            }

            if (devices.Count == 0)
            {
                AddDeviceItem(new ToolStripMenuItem("No devices") { Enabled = false });
                return;
            }

            var inputs = devices.Where(d => d.IsInput)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var outputs = devices.Where(d => !d.IsInput)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var device in inputs)
                AddDeviceItem(CreateDeviceItem(device, IsSelected(device, current)));

            // Only worth a divider when there is something on both sides of it.
            if (inputs.Length > 0 && outputs.Length > 0)
                AddDeviceItem(new ToolStripSeparator());

            foreach (var device in outputs)
                AddDeviceItem(CreateDeviceItem(device, IsSelected(device, current)));
        }

        private ToolStripMenuItem CreateDeviceItem(SonarDevice device, bool selected)
        {
            var item = new ToolStripMenuItem(device.Name)
            {
                // Checked is set here and never toggled by the click itself, so
                // clicking a device routes Sonar to it rather than letting the
                // check mark drift away from what Sonar is actually doing.
                Checked = selected,
                Tag = device
            };

            item.Click += (_, _) => OnDeviceSelected(device);
            return item;
        }

        /// <summary>
        /// Whether a device is the one currently in effect. An output counts as
        /// selected if either speaker channel points at it, matching how the
        /// volume keys treat a device as a target.
        /// </summary>
        private static bool IsSelected(SonarDevice device, SonarRedirections current) =>
            device.IsInput
                ? IsSameDevice(device.Id, current.Mic)
                : IsSameDevice(device.Id, current.Game) || IsSameDevice(device.Id, current.Chat);

        private static bool IsSameDevice(string? a, string? b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private void OnDeviceSelected(SonarDevice device)
        {
            try
            {
                if (device.IsInput)
                {
                    Log.Info($"Routing the Sonar mic input to '{device.Name}'.");
                    _bridge.SetMicDevice(device.Id);
                }
                else
                {
                    Log.Info($"Routing the Sonar chat and game outputs to '{device.Name}'.");
                    _bridge.SetGameAndChatDevice(device.Id);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Could not route Sonar to '{device.Name}'.", ex);
                return;
            }

            RefreshDeviceChecks();
        }

        /// <summary>
        /// Re-reads the redirections and moves the check marks to match, so the
        /// menu never claims something Sonar did not actually apply.
        /// </summary>
        private void RefreshDeviceChecks()
        {
            SonarRedirections current;

            try
            {
                current = _bridge.GetRedirections();
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not re-read the Sonar redirections: {ex.Message}");
                return;
            }

            foreach (var item in _deviceItems)
            {
                if (item is ToolStripMenuItem { Tag: SonarDevice device } menuItem)
                    menuItem.Checked = IsSelected(device, current);
            }
        }

        private void AddDeviceItem(ToolStripItem item)
        {
            // Always inserted directly above the lower separator so the lists
            // stay in order no matter how many devices there are.
            _menu.Items.Insert(_menu.Items.IndexOf(_deviceSeparatorBottom), item);
            _deviceItems.Add(item);
        }

        private void ClearDeviceItems()
        {
            foreach (var item in _deviceItems)
            {
                _menu.Items.Remove(item);
                item.Dispose();
            }

            _deviceItems.Clear();
        }

        /// <summary>
        /// Called when a second copy of the app is launched. Runs on a background
        /// thread, so the NotifyIcon work is pushed onto the UI thread.
        /// </summary>
        public void NudgeToFront()
        {
            void OnUiThread()
            {
                try
                {
                    _notifyIcon.BalloonTipTitle = AlreadyRunningTitle;
                    _notifyIcon.BalloonTipText =
                        "Look for the icon in the notification area.";
                    _notifyIcon.ShowBalloonTip(BalloonTipTimeout);
                    _notifyIcon.BalloonTipTitle = string.Empty;
                    _notifyIcon.BalloonTipText = string.Empty;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not raise the already-running notification: {ex.Message}");
                }
            }

            // NotificationArea posts to a Control's message loop, so hand the work
            // to a hidden control rather than marshalling by hand.
            if (_dispatch.InvokeRequired)
                _dispatch.BeginInvoke(OnUiThread);
            else
                OnUiThread();
        }

        private void RefreshStatus()
        {
            var (status, detail) = _bridge.GetStatus();

            if (status == _lastStatus && detail == _lastDetail)
                return;

            ApplyStatus(status, detail);
        }

        private void ApplyStatus(BridgeStatus status, string detail)
        {
            _lastStatus = status;
            _lastDetail = detail;
            _statusItem.Text = detail;

            // NotifyIcon throws if the tooltip runs past the shell's limit.
            var toolTip = $"{AppName} - {detail}";
            _notifyIcon.Text = toolTip.Length <= MaxToolTipLength
                ? toolTip
                : toolTip[..MaxToolTipLength];
        }

        private void OnAutoStartToggled(object? sender, EventArgs e)
        {
            // Guards the corrective write below from re-entering itself.
            if (_updatingAutoStart)
                return;

            var desired = _autoStartItem.Checked;

            if (AutoStart.Set(desired))
                return;

            _updatingAutoStart = true;

            try
            {
                _autoStartItem.Checked = !desired;
            }
            finally
            {
                _updatingAutoStart = false;
            }
        }

        /// <summary>
        /// Loads the multi-resolution icon built from "SonarDeck Icon.png" out of
        /// the embedded resources, requesting the frame size Windows will actually
        /// draw in the notification area so it stays crisp instead of being a
        /// downscaled default frame.
        /// </summary>
        private static Icon LoadIcon()
        {
            try
            {
                using var resource = typeof(TrayApplicationContext).Assembly
                    .GetManifestResourceStream(IconResourceName);

                if (resource is null)
                {
                    Log.Error($"Embedded icon resource '{IconResourceName}' is missing.");
                    return (Icon)SystemIcons.Application.Clone();
                }

                using var buffer = new MemoryStream();
                resource.CopyTo(buffer);

                var dpi = DpiX();

                // The notification area draws at roughly 16px at 100% scaling and
                // grows with DPI; the .ico carries a frame for each of those steps.
                var side = Math.Clamp((int)Math.Round(16 * dpi / 96.0), 16, 256);

                using var sized = new MemoryStream(buffer.ToArray());
                var icon = new Icon(sized, side, side);

                Log.Info($"Tray icon loaded at {side}x{side} (DPI {dpi}).");
                return icon;
            }
            catch (Exception ex)
            {
                Log.Error("Could not load the tray icon, falling back to a system icon.", ex);
                return (Icon)SystemIcons.Application.Clone();
            }
        }

        private static int DpiX()
        {
            using var probe = Graphics.FromHwnd(IntPtr.Zero);
            return (int)Math.Round(probe.DpiX);
        }

        protected override void ExitThreadCore()
        {
            if (!_cleanedUp)
            {
                _cleanedUp = true;

                _bridge.VolumeTargetsChanged -= OnVolumeTargetsChanged;
                _volume.Changed -= OnVolumeChanged;
                RemoveMediaKeyHook();

                _overlay?.Dispose();
                _overlay = null;

                _volume.Dispose();

                _statusTimer.Stop();
                _statusTimer.Dispose();
                _dispatch.Dispose();

                _menu.Dispose();

                // Hide before disposing, otherwise the icon lingers in the tray
                // until the next mouse hover.
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();

                AppIcon.Dispose();

                _bridge.Dispose();
            }

            base.ExitThreadCore();
        }
    }
}