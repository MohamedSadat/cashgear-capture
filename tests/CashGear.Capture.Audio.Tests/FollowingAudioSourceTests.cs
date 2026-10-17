using CashGear.Capture.Core.Recording;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;

namespace CashGear.Capture.Audio.Tests;

public sealed class FollowingAudioSourceTests
{
    private readonly FakeEvents _events = new();
    private readonly Dictionary<string, FakeEndpoint> _connected = [];
    private readonly List<FakeEndpoint> _opened = [];
    private readonly List<(AudioSourceStatus Status, string Message)> _statuses = [];
    private readonly List<(float First, bool Discontinuity)> _packets = [];
    private string? _default;

    [Fact]
    public void Following_the_default_switches_to_the_new_default_and_marks_the_jump()
    {
        Connect("{h5}", "system audio (Headphones (H5))");
        Connect("{realtek}", "system audio (Speakers (Realtek))");
        _default = "{h5}";
        using var source = Create(wanted: null);
        var first = _opened.Single();
        first.Deliver(0.1f);

        _default = "{realtek}";
        _events.RaiseDefaultChanged(DataFlow.Render, "{realtek}");

        first.Disposed.Should().BeTrue("the old device is closed before the new one starts");
        var second = _opened.Last();
        second.Name.Should().Be("system audio (Speakers (Realtek))");
        second.Started.Should().BeTrue();
        source.Name.Should().Be("system audio (Speakers (Realtek))");
        _statuses.Should().ContainSingle().Which.Should().Be((AudioSourceStatus.Switched, "System audio now comes from Speakers (Realtek), the new default output device."));

        first.Deliver(0.2f); // a packet the old device still had: dropped
        second.Deliver(0.3f);
        second.Deliver(0.4f);
        _packets.Should().Equal((0.1f, false), (0.3f, true), (0.4f, false));
    }

    [Fact]
    public void A_change_of_the_other_direction_or_to_the_same_device_is_ignored()
    {
        Connect("{h5}", "system audio (Headphones (H5))");
        _default = "{h5}";
        using var source = Create(wanted: null);

        _events.RaiseDefaultChanged(DataFlow.Capture, "{mic}");
        _events.RaiseDefaultChanged(DataFlow.Render, "{h5}");

        _opened.Should().ContainSingle().Which.Disposed.Should().BeFalse();
        _statuses.Should().BeEmpty();
    }

    [Fact]
    public void When_the_default_goes_and_none_takes_over_the_loss_is_reported_once_at_the_next_retry()
    {
        Connect("{h5}", "system audio (Headphones (H5))");
        _default = "{h5}";
        using var source = Create(wanted: null);
        var first = _opened.Single();

        Unplug("{h5}");
        _default = null;
        first.Fail(new InvalidOperationException("Device invalidated"));
        _statuses.Should().BeEmpty("Windows names a new default a moment later, so the loss waits for the first retry");
        source.IsRecordingDevice.Should().BeFalse();

        source.Retry();
        source.Retry();
        _statuses.Should().ContainSingle().Which.Status.Should().Be(AudioSourceStatus.Lost);
        _statuses[0].Message.Should().Contain("Headphones (H5)").And.Contain("silent");

        Connect("{realtek}", "system audio (Speakers (Realtek))");
        _default = "{realtek}";
        _events.RaiseDefaultChanged(DataFlow.Render, "{realtek}");
        _statuses.Should().HaveCount(2);
        _statuses[1].Status.Should().Be(AudioSourceStatus.Switched);
        source.IsRecordingDevice.Should().BeTrue();
    }

    [Fact]
    public void A_new_default_named_right_after_the_old_one_fails_is_only_reported_as_a_switch()
    {
        Connect("{h5}", "system audio (Headphones (H5))");
        Connect("{realtek}", "system audio (Speakers (Realtek))");
        _default = "{h5}";
        using var source = Create(wanted: null);

        Unplug("{h5}");
        _opened.Single().Fail(new InvalidOperationException("Device invalidated"));
        _default = "{realtek}";
        _events.RaiseDefaultChanged(DataFlow.Render, "{realtek}");
        source.Retry();

        _statuses.Should().ContainSingle().Which.Status.Should().Be(AudioSourceStatus.Switched);
    }

    [Fact]
    public void No_default_at_all_is_reported_and_the_next_default_is_recorded()
    {
        Connect("{h5}", "Microphone (H5)");
        _default = "{h5}";
        using var source = Create(wanted: null, flow: DataFlow.Capture, kind: RecordingSound.Microphone);

        Unplug("{h5}");
        _default = null;
        _events.RaiseDefaultChanged(DataFlow.Capture, null);

        _statuses.Should().ContainSingle().Which.Status.Should().Be(AudioSourceStatus.Lost);
        _opened.Single().Disposed.Should().BeTrue();

        Connect("{usb}", "Microphone (USB)");
        _default = "{usb}";
        _events.RaiseDefaultChanged(DataFlow.Capture, "{usb}");

        _statuses[1].Should().Be((AudioSourceStatus.Switched, "The microphone recorded is now Microphone (USB), the new default microphone."));
    }

    [Fact]
    public void A_chosen_device_that_is_unplugged_is_reported_at_once_and_recorded_again_when_it_is_back()
    {
        Connect("{h5}", "Microphone (H5)");
        Connect("{usb}", "Microphone (USB)");
        using var source = Create(wanted: "{h5}", flow: DataFlow.Capture, kind: RecordingSound.Microphone);
        var first = _opened.Single();

        Unplug("{h5}");
        _events.RaiseDeparted("{h5}");
        first.Fail(new InvalidOperationException("Device invalidated")); // the capture thread notices too: reported once

        _statuses.Should().ContainSingle().Which.Should().Be((AudioSourceStatus.Lost, "Microphone (H5) was disconnected. Its sound is silent in the recording until it is back."));
        first.Disposed.Should().BeTrue();

        _events.RaiseArrived("{usb}");
        _events.RaiseDefaultChanged(DataFlow.Capture, "{usb}");
        _opened.Should().ContainSingle("another device, or a new default, isn't the one chosen");

        Connect("{h5}", "Microphone (H5)");
        _events.RaiseArrived("{h5}");

        _statuses.Should().HaveCount(2);
        _statuses[1].Should().Be((AudioSourceStatus.Restored, "Microphone (H5) works again and is being recorded."));
        _opened.Last().Deliver(0.5f);
        _packets.Should().ContainSingle().Which.Should().Be((0.5f, true));
    }

    [Fact]
    public void A_chosen_device_missing_at_the_start_is_recorded_once_it_is_connected()
    {
        using var source = Create(wanted: "{h5}", flow: DataFlow.Capture, kind: RecordingSound.Microphone);
        source.Name.Should().Be("Microphone");
        source.IsRecordingDevice.Should().BeFalse();

        source.Retry();
        _statuses.Should().BeEmpty("the recording's note already says it isn't connected");

        Connect("{h5}", "Microphone (H5)");
        source.Retry();

        _statuses.Should().ContainSingle().Which.Should().Be((AudioSourceStatus.Restored, "Microphone (H5) is connected now and is being recorded."));
        source.Name.Should().Be("Microphone (H5)");
    }

    [Fact]
    public void A_device_that_fails_without_any_change_is_tried_again()
    {
        Connect("{h5}", "Microphone (H5)");
        using var source = Create(wanted: "{h5}", flow: DataFlow.Capture, kind: RecordingSound.Microphone);

        _opened.Single().Fail(new InvalidOperationException("The audio service restarted"));
        _statuses.Should().ContainSingle().Which.Message.Should().Contain("stopped working (The audio service restarted)");

        source.Retry();
        _statuses.Should().HaveCount(2);
        _statuses[1].Status.Should().Be(AudioSourceStatus.Restored);
        _opened.Should().HaveCount(2);
    }

    [Fact]
    public void Disposing_closes_the_device_and_stops_listening()
    {
        Connect("{h5}", "system audio (Headphones (H5))");
        _default = "{h5}";
        var source = Create(wanted: null);

        source.Dispose();
        _opened.Single().Disposed.Should().BeTrue();
        _events.Subscribers.Should().Be(0);

        _events.RaiseDefaultChanged(DataFlow.Render, "{h5}");
        source.Retry();
        _opened.Should().ContainSingle();
    }

    private FollowingAudioSource Create(string? wanted, DataFlow flow = DataFlow.Render, RecordingSound kind = RecordingSound.System)
    {
        var source = new FollowingAudioSource(
            kind, flow, wanted, Open, _events, Open(wanted), kind == RecordingSound.Microphone ? "Microphone" : "System audio",
            NullLogger.Instance, Timeout.InfiniteTimeSpan);
        source.Start((samples, _, discontinuity) => _packets.Add((samples[0], discontinuity)), (status, message) => _statuses.Add((status, message)));
        return source;
    }

    private EndpointOpened Open(string? id)
    {
        id ??= _default;
        if (id is null || !_connected.TryGetValue(id, out var device))
        {
            return new EndpointOpened(null, null, Missing: "not connected");
        }

        var endpoint = new FakeEndpoint(device.Name);
        _opened.Add(endpoint);
        return new EndpointOpened(endpoint, id);
    }

    private void Connect(string id, string name) => _connected[id] = new FakeEndpoint(name);

    private void Unplug(string id) => _connected.Remove(id);

    private sealed class FakeEndpoint(string name) : IAudioEndpoint
    {
        private AudioDataHandler? _data;
        private Action<Exception>? _failed;

        public string Name => name;

        public bool Started { get; private set; }

        public bool Disposed { get; private set; }

        public void Start(AudioDataHandler data, Action<Exception> failed)
        {
            Started = true;
            _data = data;
            _failed = failed;
        }

        public void Deliver(float value) => _data!([value, value], 0, discontinuity: false);

        public void Fail(Exception ex) => _failed!(ex);

        public void Dispose() => Disposed = true;
    }

    /// <summary>Device events raised by the test; posted work runs at once.</summary>
    private sealed class FakeEvents : IAudioDeviceEvents
    {
        private Action<DataFlow, string?>? _defaultChanged;
        private Action<string>? _arrived;
        private Action<string>? _departed;

        public event Action<DataFlow, string?>? DefaultChanged
        {
            add => _defaultChanged += value;
            remove => _defaultChanged -= value;
        }

        public event Action<string>? Arrived
        {
            add => _arrived += value;
            remove => _arrived -= value;
        }

        public event Action<string>? Departed
        {
            add => _departed += value;
            remove => _departed -= value;
        }

        public int Subscribers =>
            (_defaultChanged?.GetInvocationList().Length ?? 0) + (_arrived?.GetInvocationList().Length ?? 0) + (_departed?.GetInvocationList().Length ?? 0);

        public void Post(Action work) => work();

        public void RaiseDefaultChanged(DataFlow flow, string? id) => _defaultChanged?.Invoke(flow, id);

        public void RaiseArrived(string id) => _arrived?.Invoke(id);

        public void RaiseDeparted(string id) => _departed?.Invoke(id);
    }
}
