using CashGear.Capture.App.SingleInstance;
using CashGear.Capture.Core.Instance;

namespace CashGear.Capture.App.Tests.SingleInstance;

public sealed class HandOffMessagesTests
{
    [Fact]
    public void Every_command_says_the_running_copy_is_elevated_and_why_it_cannot_be_reached()
    {
        foreach (var command in Enum.GetValues<InstanceCommand>())
        {
            HandOffMessages.InstanceElevated(command).Should()
                .Contain("running as administrator").And
                .Contain("Windows only lets programs that also run as administrator talk to it");
        }
    }

    [Fact]
    public void Exit_says_how_the_elevated_copy_can_be_closed()
    {
        HandOffMessages.InstanceElevated(InstanceCommand.Exit).Should()
            .Contain("--exit").And
            .Contain("tray icon").And
            .Contain("run the command again as administrator");
    }

    [Fact]
    public void A_capture_command_names_its_switch()
    {
        HandOffMessages.InstanceElevated(InstanceCommand.CaptureRegion).Should().Contain("--capture-region can't reach it");
        HandOffMessages.InstanceElevated(InstanceCommand.Record).Should().Contain("--record can't reach it");
    }
}
