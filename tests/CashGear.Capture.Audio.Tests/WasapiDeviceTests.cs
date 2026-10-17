using CashGear.Capture.Core.Recording;
using CashGear.Capture.Engine.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Audio.Tests;

/// <summary>The real sound devices of this PC (skipped without an interactive session or an output device).</summary>
[Trait("Category", InteractiveDesktop.Category)]
public sealed class WasapiDeviceTests
{
    [Fact]
    public void The_default_output_device_is_among_the_output_devices()
    {
        InteractiveDesktop.Require();
        var devices = new WasapiAudioDevices(NullLogger<WasapiAudioDevices>.Instance);
        var speakers = devices.DefaultOutput();
        Assert.SkipWhen(speakers is null, "This PC has no sound output device.");

        devices.Outputs().Should().Contain(speakers);
        devices.Microphones().Should().NotContain(speakers);
    }

    [Fact(Timeout = 30_000)]
    public async Task System_audio_of_the_default_device_is_opened_and_follows_it()
    {
        InteractiveDesktop.Require();
        Assert.SkipWhen(new WasapiAudioDevices(NullLogger<WasapiAudioDevices>.Instance).DefaultOutput() is null, "This PC has no sound output device.");
        using var watcher = new AudioDeviceWatcher(NullLogger<AudioDeviceWatcher>.Instance);
        var factory = new WasapiAudioSourceFactory(watcher, NullLoggerFactory.Instance);

        var opened = factory.OpenSystemAudio(device: null);

        opened.Note.Should().BeNull();
        using var source = opened.Source.Should().BeOfType<FollowingAudioSource>().Subject;
        source.Kind.Should().Be(RecordingSound.System);
        source.Name.Should().StartWith("system audio (");
        var statuses = new List<AudioSourceStatus>();
        source.Start((_, _, _) => { }, (status, _) => statuses.Add(status));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        source.IsRecordingDevice.Should().BeTrue();
        statuses.Should().BeEmpty();
    }

    [Fact]
    public void A_chosen_device_that_does_not_exist_waits_with_a_note()
    {
        InteractiveDesktop.Require();
        using var watcher = new AudioDeviceWatcher(NullLogger<AudioDeviceWatcher>.Instance);
        var factory = new WasapiAudioSourceFactory(watcher, NullLoggerFactory.Instance);

        var opened = factory.OpenMicrophone("{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}");

        opened.Note.Should().Contain("isn't connected");
        using var source = opened.Source.Should().BeOfType<FollowingAudioSource>().Subject;
        source.IsRecordingDevice.Should().BeFalse();
        source.Name.Should().Be("Microphone");
    }
}
