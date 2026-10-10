using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;
using SteelSeriesAPI.Sonar;
using SteelSeriesAPI.Sonar.Enums;

namespace SonarController
{
    internal enum BridgeStatus
    {
        Searching,
        Connected,
        Disconnected,
        Error
    }

    /// <summary>
    /// A physical Windows audio endpoint Sonar can point a channel at. Input
    /// devices are capture endpoints (microphones); outputs are render
    /// endpoints. Sonar's own virtual devices are never represented here.
    /// </summary>
    internal readonly record struct SonarDevice(string Id, string Name, bool IsInput);

    /// <summary>
    /// The physical device each channel the tray menu can retarget currently
    /// resolves to, or null when Sonar has no redirection for it.
    /// </summary>
    internal readonly record struct SonarRedirections(string? Mic, string? Game, string? Chat);

    /// <summary>
    /// Owns the link between SteelSeries Sonar and the ESP8266 on the serial
    /// port: discovers the device, mirrors ChatMix / mute state to it, and
    /// applies the commands coming back the other way.
    ///
    /// Everything runs off the UI thread and reconnects on its own, so the app
    /// can sit in the tray indefinitely whether or not the hardware is present.
    /// </summary>
    internal sealed class SonarBridge : IDisposable
    {
        private const int BaudRate = 115200;

        // Opening the serial port can reset the ESP, so hold off until it has
        // finished booting and is listening before pushing state at it.
        private static readonly TimeSpan EspBootDelay = TimeSpan.FromSeconds(2);

        private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(2);

        private readonly object _gate = new();

        private SerialPort? _serial;
        private SonarClient? _sonar;
        private CancellationTokenSource? _cts;
        private Task? _worker;
        private int _restartRequested;
        private bool _disposed;

        private BridgeStatus _status = BridgeStatus.Searching;
        private string _statusDetail = "Starting up";

        /// <summary>
        /// Raised when the set of Windows playback devices behind the Game and
        /// Chat channels changes, and again with an empty set when Sonar goes
        /// away. Fired on the bridge worker thread, so subscribers must marshal.
        /// </summary>
        public event Action<string[]>? VolumeTargetsChanged;

        /// <summary>
        /// Current link state and its human-readable detail, read together so
        /// callers never observe a status and a description from different states.
        /// </summary>
        public (BridgeStatus Status, string Detail) GetStatus()
        {
            lock (_gate)
                return (_status, _statusDetail);
        }

        /// <summary>
        /// Reads the physical devices Sonar knows about together with the
        /// redirections currently in effect, for the tray menu to present.
        ///
        /// This deliberately uses its own short-lived client rather than the
        /// session's, so it works whether or not the bridge is connected and
        /// never contends with the event polling. It does block on local Sonar
        /// calls, so call it from the UI only where a brief pause is expected.
        /// </summary>
        public (IReadOnlyList<SonarDevice> Devices, SonarRedirections Redirections) GetDeviceState()
        {
            using var sonar = new SonarClient();

            var devices = sonar.Devices.GetAllAsync().GetAwaiter().GetResult()
                .Where(d => !d.IsSonarVirtual)
                .Select(d => new SonarDevice(d.Id, d.Name, d.DataFlow == AudioDataFlow.Capture))
                .ToArray();

            return (devices, ReadRedirections(sonar));
        }

        /// <summary>
        /// Re-reads just the redirections, for refreshing the menu's check marks
        /// after a change without paying for the full device list again.
        /// </summary>
        public SonarRedirections GetRedirections()
        {
            using var sonar = new SonarClient();
            return ReadRedirections(sonar);
        }

        /// <summary>Points Sonar's microphone channel at a physical input.</summary>
        public void SetMicDevice(string deviceId)
        {
            using var sonar = new SonarClient();
            sonar.Redirections.SetClassicDeviceAsync(Channel.Mic, deviceId).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Points both of Sonar's speaker channels (Chat and Game) at the same
        /// physical output, which is what the volume keys follow.
        /// </summary>
        public void SetGameAndChatDevice(string deviceId)
        {
            using var sonar = new SonarClient();
            sonar.Redirections.SetClassicDeviceAsync(Channel.Game, deviceId).GetAwaiter().GetResult();
            sonar.Redirections.SetClassicDeviceAsync(Channel.Chat, deviceId).GetAwaiter().GetResult();
        }

        private static SonarRedirections ReadRedirections(SonarClient sonar)
        {
            var classic = sonar.Redirections.GetClassicRedirectionsAsync().GetAwaiter().GetResult();

            string? DeviceFor(Channel channel) =>
                classic.FirstOrDefault(r => r.Channel == channel)?.DeviceId;

            return new SonarRedirections(
                DeviceFor(Channel.Mic),
                DeviceFor(Channel.Game),
                DeviceFor(Channel.Chat));
        }

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => RunAsync(_cts.Token));
        }

        /// <summary>
        /// Drops the current session and immediately looks for the device again.
        /// </summary>
        public void Restart()
        {
            if (_disposed)
                return;

            Log.Info("Restart requested.");

            // Picked up by the health check, which tears the session down so the
            // connect loop can bring it straight back up.
            Interlocked.Exchange(ref _restartRequested, 1);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
            }

            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }

            try
            {
                _worker?.Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException)
            {
                // The worker logged whatever went wrong on its way out.
            }

            _cts?.Dispose();
            Disconnect();
        }

        private void SetStatus(BridgeStatus status, string detail)
        {
            lock (_gate)
            {
                _status = status;
                _statusDetail = detail;
            }
        }

        private async Task RunAsync(CancellationToken token)
        {
            Log.Info("SonarDeck starting.");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndWatchAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error("Connection cycle failed, will retry.", ex);
                    SetStatus(BridgeStatus.Error, ex.Message);
                    Disconnect();
                }

                if (token.IsCancellationRequested)
                    break;

                // A manual restart should come straight back up rather than
                // sitting through the retry delay.
                if (Interlocked.Exchange(ref _restartRequested, 0) != 0)
                    continue;

                try
                {
                    await Task.Delay(RetryInterval, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            Log.Info("Connection worker stopped.");
        }

        private async Task ConnectAndWatchAsync(CancellationToken token)
        {
            var port = FindEspComPort();

            if (port is null)
            {
                Log.Info("ESP8266 not found, waiting for it to appear.");
                SetStatus(BridgeStatus.Searching, "Looking for ESP8266");
                return;
            }

            Connect(port);

            Log.Info($"ESP8266 found on {port}.");
            SetStatus(BridgeStatus.Connected, $"Connected - {port}");

            await SyncCurrentStateAsync(token);

            // Stay connected until the port drops or the user asks for a restart.
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(HealthCheckInterval, token);

                if (Volatile.Read(ref _restartRequested) != 0)
                {
                    Log.Info("Restarting connection.");
                    Disconnect();
                    return;
                }

                if (IsSerialOpen())
                    continue;

                Log.Warn($"Serial port {port} closed unexpectedly, reconnecting.");
                SetStatus(BridgeStatus.Disconnected, $"Lost {port}");
                Disconnect();
                return;
            }
        }

        /// <summary>
        /// Pushes the current ChatMix balance and mic mute state to the ESP once
        /// it has finished booting. The ESP keeps both in RAM and loses them on
        /// its own reset, and we only stream changes while connected, so without
        /// this it would sit showing stale values until the user touched a slider.
        /// </summary>
        private async Task SyncCurrentStateAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(EspBootDelay, token);

                using var sonar = new SonarClient();

                var balance = (await sonar.ChatMix.GetAsync()).Balance;
                var muted = (await sonar.VolumeSettings.GetAsync(Channel.Mic)).Muted;

                if (!IsSerialOpen())
                {
                    Log.Info("ESP disconnected during boot delay, skipping initial state sync.");
                    return;
                }

                WriteToEsp($"ChatMix:{balance:F2}");
                WriteToEsp($"MUTED:{Convert.ToInt32(muted)}");

                Log.Info($"Sent initial state: ChatMix {balance:F2}, mic muted {muted}.");
            }
            catch (OperationCanceledException)
            {
                throw; // shutting down, not a failure
            }
            catch (Exception ex)
            {
                // Never fatal: the next change event will correct the ESP anyway.
                Log.Error("Could not send the initial ChatMix / mute state to the ESP.", ex);
            }
        }

        private void Connect(string port)
        {
            var serial = new SerialPort(port, BaudRate) { NewLine = "\n" };
            serial.DataReceived += OnSerialDataReceived;

            try
            {
                serial.Open();
            }
            catch
            {
                serial.DataReceived -= OnSerialDataReceived;
                serial.Dispose();
                throw;
            }

            var sonar = new SonarClient();

            try
            {
                // Sonar event detection
                sonar.Events.PollingInterval = TimeSpan.FromMilliseconds(200);
                sonar.Events.ChatMixChanged += OnChatMixChanged;
                sonar.Events.VolumeChanged += OnVolumeChanged;
                sonar.Events.ClassicDeviceChanged += OnClassicDeviceChanged;
                sonar.Events.Connected += OnSonarConnected;
                sonar.Events.Disconnected += OnSonarDisconnected;
                sonar.Events.Start();
            }
            catch
            {
                sonar.Events.ChatMixChanged -= OnChatMixChanged;
                sonar.Events.VolumeChanged -= OnVolumeChanged;
                sonar.Events.ClassicDeviceChanged -= OnClassicDeviceChanged;
                sonar.Events.Connected -= OnSonarConnected;
                sonar.Events.Disconnected -= OnSonarDisconnected;
                sonar.Dispose();

                serial.DataReceived -= OnSerialDataReceived;
                serial.Dispose();
                throw;
            }

            lock (_gate)
            {
                _serial = serial;
                _sonar = sonar;
            }

            LogDevices(sonar);
            PublishVolumeTargets(sonar);
        }

        private static void LogDevices(SonarClient sonar)
        {
            try
            {
                var devices = sonar.Devices.GetAllAsync()
                    .GetAwaiter().GetResult()
                    .Where(x => !x.IsSonarVirtual)
                    .Select(x => x.Name);

                Log.Info($"Sonar devices: {string.Join(", ", devices)}");
            }
            catch (Exception ex)
            {
                // Purely informational, never worth failing a connection over.
                Log.Warn($"Could not list Sonar devices: {ex.Message}");
            }
        }

        /// <summary>
        /// Works out which Windows playback endpoints Sonar is sending the Game
        /// and Chat channels out to and hands their IDs to whoever owns the media
        /// key hook. Game and Chat usually share one device, but are kept
        /// separate in case the user routes them differently.
        /// </summary>
        private void PublishVolumeTargets(SonarClient sonar)
        {
            try
            {
                var classic = sonar.Redirections.GetClassicRedirectionsAsync().GetAwaiter().GetResult();

                var targets = classic
                    .Where(r => r.Channel is Channel.Game or Channel.Chat)
                    .Select(r => r.DeviceId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()!;

                VolumeTargetsChanged?.Invoke(targets);
            }
            catch (Exception ex)
            {
                // Losing this only costs the volume keys, never the bridge itself.
                Log.Error("Could not determine which Windows device the volume keys should control.", ex);
                VolumeTargetsChanged?.Invoke([]);
            }
        }

        private void Disconnect()
        {
            SerialPort? serial;
            SonarClient? sonar;

            lock (_gate)
            {
                serial = _serial;
                sonar = _sonar;
                _serial = null;
                _sonar = null;
            }

            if (serial is not null)
            {
                try
                {
                    serial.DataReceived -= OnSerialDataReceived;

                    if (serial.IsOpen)
                        serial.Close();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not close serial port cleanly: {ex.Message}");
                }
                finally
                {
                    serial.Dispose();
                }
            }

            // Hands the volume keys back to Windows while Sonar is not running, whether or
            // not there was a session to tear down.
            VolumeTargetsChanged?.Invoke([]);

            if (sonar is null)
                return;

            try
            {
                sonar.Events.ChatMixChanged -= OnChatMixChanged;
                sonar.Events.VolumeChanged -= OnVolumeChanged;
                sonar.Events.ClassicDeviceChanged -= OnClassicDeviceChanged;
                sonar.Events.Connected -= OnSonarConnected;
                sonar.Events.Disconnected -= OnSonarDisconnected;
                sonar.Events.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not stop Sonar polling cleanly: {ex.Message}");
            }
            finally
            {
                sonar.Dispose();
            }
        }

        private void OnChatMixChanged(object? sender, SteelSeriesAPI.Sonar.Models.ChatMixSetting e)
        {
            Log.Info($"ChatMix changed: {e.Balance:F2}");
            WriteToEsp($"ChatMix:{e.Balance:F2}");
        }

        private void OnVolumeChanged(object? sender, SteelSeriesAPI.Sonar.Events.VolumeChange e)
        {
            if (e.Channel == Channel.Mic)
                WriteToEsp($"MUTED:{Convert.ToInt32(e.IsMuted)}");
        }

        private void OnSonarConnected(object? sender, EventArgs e) =>
            Log.Info("Sonar connected.");

        /// <summary>
        /// Fired when the user points a Sonar channel at a different Windows
        /// device. The event names only the one channel that moved, so the whole
        /// Game/Chat set is re-read rather than trusting the event payload: a
        /// single change can leave the two channels pointing at different
        /// devices, and the volume keys have to follow whatever Sonar settled on.
        /// </summary>
        private void OnClassicDeviceChanged(object? sender, SteelSeriesAPI.Sonar.Events.ClassicDeviceChange e)
        {
            if (e.Channel is not (Channel.Game or Channel.Chat))
                return;

            Log.Info($"Sonar {e.Channel} speakers changed, re-reading redirections.");

            SonarClient? sonar;

            lock (_gate)
                sonar = _sonar;

            if (sonar is not null)
                PublishVolumeTargets(sonar);
        }

        private void OnSonarDisconnected(object? sender, EventArgs e) =>
            Log.Warn("Sonar disconnected, ChatMix updates paused.");

        private bool IsSerialOpen()
        {
            lock (_gate)
                return _serial is { IsOpen: true };
        }

        private void WriteToEsp(string message)
        {
            SerialPort? serial;

            lock (_gate)
                serial = _serial;

            if (serial is null || !serial.IsOpen)
            {
                Log.Warn($"Dropped '{message}', ESP is not connected.");
                return;
            }

            try
            {
                serial.WriteLine(message);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not write '{message}' to ESP: {ex.Message}");
            }
        }

        private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            SerialPort? serial;

            lock (_gate)
                serial = _serial;

            if (serial is null || !serial.IsOpen)
                return;

            string command;

            try
            {
                command = serial.ReadLine().Trim();
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException)
            {
                Log.Warn($"Dropped incomplete serial read: {ex.Message}");
                return;
            }

            if (command.Length == 0)
                return;

            Log.Info($"ESP: {command}");

            try
            {
                HandleCommand(command);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to handle command '{command}'", ex);
            }
        }

        private void HandleCommand(string command)
        {
            // A dedicated client per command, so commands never contend with the
            // event polling happening on the long-lived one.
            using var sonar = new SonarClient();
            var currentBalance = sonar.ChatMix.GetAsync().Result.Balance;

            switch (command)
            {
                case "CHAT_PLUS":
                    Log.Info("ChatMix +");
                    if (currentBalance < 0.9)
                        sonar.ChatMix.SetAsync(currentBalance + 0.1).Wait();
                    else
                        sonar.ChatMix.SetAsync(1.0).Wait();
                    break;

                case "CHAT_MINUS":
                    Log.Info("ChatMix -");
                    if (currentBalance > -0.9)
                        sonar.ChatMix.SetAsync(currentBalance - 0.1).Wait();
                    else
                        sonar.ChatMix.SetAsync(-1).Wait();
                    break;

                case "SWITCH_MIC":
                    var allDevices = sonar.Devices.GetAllAsync().GetAwaiter().GetResult();
                    var filteredDevices = allDevices.Where(x => !x.IsSonarVirtual && x.Name.Contains("Microphone", StringComparison.OrdinalIgnoreCase));
                    foreach (var device in filteredDevices)
                        Log.Info($"Found device: {device.Name}");

                    if (filteredDevices.Count() > 1)
                    {
                        var currentDeviceId = sonar.Redirections.GetClassicRedirectionsAsync().GetAwaiter().GetResult().Single(x => x.Channel == Channel.Mic).DeviceId;
                        var currentDevice = filteredDevices.Single(x => x.Id == currentDeviceId);
                        if (currentDevice.Name.Contains("CETRA", StringComparison.OrdinalIgnoreCase))
                            sonar.Redirections.SetClassicDeviceAsync(Channel.Mic, filteredDevices.Single(x => x.Name.Contains("Microphone (2- fifine Microphone)", StringComparison.OrdinalIgnoreCase)).Id).GetAwaiter().GetResult();
                        else
                            sonar.Redirections.SetClassicDeviceAsync(Channel.Mic, filteredDevices.Single(x => x.Name.Contains("CETRA", StringComparison.OrdinalIgnoreCase)).Id).GetAwaiter().GetResult();
                    }
                    break;

                case "MUTE":
                    Log.Info("Toggle mute");
                    var currentMuteSetting = sonar.VolumeSettings.GetAsync(Channel.Mic).Result.Muted;
                    sonar.VolumeSettings.SetMuteAsync(Channel.Mic, !currentMuteSetting).GetAwaiter().GetResult();
                    break;
            }
        }

        public static string? FindEspComPort()
        {
            const string vid = "1A86";
            const string pid = "7523";

            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity " +
                "WHERE PNPClass = 'Ports'");

            foreach (ManagementObject device in searcher.Get())
            {
                var name = device["Name"]?.ToString();
                var pnpDeviceId = device["PNPDeviceID"]?.ToString();

                if (name == null || pnpDeviceId == null)
                    continue;

                if (!pnpDeviceId.Contains($"VID_{vid}", StringComparison.OrdinalIgnoreCase) ||
                    !pnpDeviceId.Contains($"PID_{pid}", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Example:
                // "USB-SERIAL CH340 (COM7)"
                var match = Regex.Match(name, @"\((COM\d+)\)$");

                if (match.Success)
                    return match.Groups[1].Value;
            }

            return null;
        }
    }
}