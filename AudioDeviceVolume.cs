using NAudio.CoreAudioApi;

namespace SonarController
{
    /// <summary>
    /// Adjusts the master volume of the Windows playback devices that Sonar is
    /// sending the Game/Chat channels out to, so the keyboard's media keys move
    /// the real speakers instead of whatever device Windows considers default.
    ///
    /// NAudio's Core Audio wrapper is used rather than hand-rolled interop:
    /// activating IAudioEndpointVolume through a raw COM declaration reliably
    /// fails with E_INVALIDARG on this machine, whereas NAudio reaches the same
    /// endpoints (and the endpoint's own step count, so a key press moves the
    /// volume by the same amount Windows itself would).
    /// </summary>
    internal sealed class AudioDeviceVolume : IDisposable
    {
        private readonly MMDeviceEnumerator _enumerator = new();

        private string[] _deviceIds = [];

        public bool HasTargets => _deviceIds.Length > 0;

        /// <summary>
        /// The level of the device the keys control, read back after a change so
        /// the on-screen indicator can show what actually happened rather than a
        /// guess. <paramref name="device"/> names the device it applies to, which
        /// matters when Game and Chat are routed differently.
        /// </summary>
        public readonly record struct State(string Device, float Level, bool Muted);

        /// <summary>
        /// Raised on the UI thread after a key press has been applied, carrying
        /// the resulting state for the indicator to display.
        /// </summary>
        public event Action<State>? Changed;

        /// <summary>
        /// Points the controller at a set of playback devices. Passing an empty
        /// set disables volume handling until new targets arrive. Returns true if
        /// the targets actually changed, so callers can avoid redundant work.
        /// </summary>
        public bool SetTargets(IEnumerable<string> deviceIds)
        {
            var updated = deviceIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (updated.Length == _deviceIds.Length &&
                updated.SequenceEqual(_deviceIds, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            _deviceIds = updated;

            Log.Info(updated.Length == 0
                ? "No playback device target for the volume keys."
                : $"Volume keys now control: {DescribeTargets()}");

            return true;
        }

        public bool StepUp() => Apply(endpoint =>
        {
            endpoint.VolumeStepUp();
            return Percent(endpoint);
        }, "up");

        public bool StepDown() => Apply(endpoint =>
        {
            endpoint.VolumeStepDown();
            return Percent(endpoint);
        }, "down");

        public bool ToggleMute() => Apply(endpoint =>
        {
            endpoint.Mute = !endpoint.Mute;
            return $"muted={endpoint.Mute}";
        }, "mute");

        /// <summary>
        /// Applies an action to every target device. All of them are attempted even
        /// if one fails, because a single unavailable endpoint (unplugged headset,
        /// for instance) should not stop the others from responding.
        /// </summary>
        private bool Apply(Func<AudioEndpointVolume, string> action, string verb)
        {
            if (_deviceIds.Length == 0)
            {
                Log.Warn($"Volume {verb} ignored: no playback device target.");
                return false;
            }

            var any = false;

            // The first target is the one the indicator reports on; Game is
            // ordered ahead of Chat when the bridge collects them.
            var reported = (State?)null;

            foreach (var deviceId in _deviceIds)
            {
                try
                {
                    // The device is resolved per press rather than cached, so one
                    // that appears or disappears is picked up without needing the
                    // target set refreshed.
                    using var device = _enumerator.GetDevice(deviceId);
                    using var endpoint = device.AudioEndpointVolume;

                    Log.Info($"Volume {verb} on {device.FriendlyName}: {action(endpoint)}");

                    reported ??= new State(device.FriendlyName, endpoint.MasterVolumeLevelScalar, endpoint.Mute);
                    any = true;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not set volume {verb} on {deviceId}: {ex.Message}");
                }
            }

            if (reported is { } state)
                Changed?.Invoke(state);

            return any;
        }

        private string DescribeTargets()
        {
            var names = _deviceIds.Select(id =>
            {
                try
                {
                    using var device = _enumerator.GetDevice(id);
                    return $"{device.FriendlyName} ({id})";
                }
                catch (Exception ex)
                {
                    return $"{id} (unavailable: {ex.Message})";
                }
            });

            return string.Join("; ", names);
        }

        private static string Percent(AudioEndpointVolume endpoint) =>
            $"{endpoint.MasterVolumeLevelScalar * 100:F0}%";

        public void Dispose() => _enumerator.Dispose();
    }
}