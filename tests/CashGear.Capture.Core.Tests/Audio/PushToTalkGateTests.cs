using CashGear.Capture.Core.Audio;
using CashGear.Capture.Core.Recording;

namespace CashGear.Capture.Core.Tests.Audio;

public sealed class PushToTalkGateTests
{
    [Fact]
    public void It_opens_at_once_and_closes_a_quarter_second_after_the_key_is_let_go()
    {
        var gate = new PushToTalkGate();
        gate.IsOpen.Should().BeFalse();

        gate.Update(keyDown: true, TimeSpan.FromSeconds(1)).Should().BeTrue();
        gate.Update(keyDown: false, TimeSpan.FromSeconds(2)).Should().BeTrue("released: held open");
        gate.Update(keyDown: false, TimeSpan.FromSeconds(2.24)).Should().BeTrue();
        gate.Update(keyDown: false, TimeSpan.FromSeconds(2.26)).Should().BeFalse();

        gate.Update(keyDown: true, TimeSpan.FromSeconds(3)).Should().BeTrue();
        gate.Update(keyDown: false, TimeSpan.FromSeconds(3.1)).Should().BeTrue();
        gate.Update(keyDown: true, TimeSpan.FromSeconds(3.2)).Should().BeTrue("pressed again: the hold starts over on the next release");
        gate.Update(keyDown: false, TimeSpan.FromSeconds(3.4)).Should().BeTrue();
        gate.Update(keyDown: false, TimeSpan.FromSeconds(3.6)).Should().BeTrue();
        gate.Update(keyDown: false, TimeSpan.FromSeconds(3.7)).Should().BeFalse();
    }

    [Fact]
    public void A_gated_track_is_silent_and_keeps_its_own_mute()
    {
        var mixer = new AudioMixer();
        var microphone = mixer.AddTrack("Microphone");
        mixer.Place(microphone, Enumerable.Repeat(0.5f, 200).ToArray(), 0);
        var output = new float[200];

        microphone.Gated = true;
        mixer.Mix(output.AsSpan(0, 100));
        output[..100].Should().OnlyContain(s => s == 0);
        microphone.Muted.Should().BeFalse("push-to-talk doesn't touch the user's mute");

        microphone.Gated = false;
        mixer.Mix(output.AsSpan(100, 100));
        output[100..].Should().OnlyContain(s => s == 0.5f);
    }
}
