using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.SingleInstance;
using CashGear.Capture.Core.Instance;

namespace CashGear.Capture.App.Tests.SingleInstance;

public sealed class InstancePipeClientTests
{
    private static readonly SecurityIdentifier User = new("S-1-5-21-1000-2000-3000-1001");
    private static readonly SecurityIdentifier OtherUser = new("S-1-5-21-1000-2000-3000-1002");
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    [Fact]
    public async Task A_command_reaches_an_instance_of_the_same_user()
    {
        var name = UniquePipeName();

        // Created like InstancePipeServer creates it.
        await using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var connected = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);

        var send = Task.Run(() => InstancePipeClient.Send(name, InstanceCommand.CaptureRegion, TimeSpan.FromSeconds(5)), TestContext.Current.CancellationToken);
        await connected;
        using var reader = new StreamReader(server, Encoding.UTF8);

        (await reader.ReadLineAsync(TestContext.Current.CancellationToken)).Should().Be("capture-region");
        (await send).Should().Be(HandOffResult.Delivered);
    }

    [Fact]
    public void No_instance_listening_is_no_answer()
    {
        InstancePipeClient.Send(UniquePipeName(), InstanceCommand.Activate, TimeSpan.FromMilliseconds(50)).Should().Be(HandOffResult.NoAnswer);
    }

    [Fact]
    public async Task A_pipe_that_shuts_this_user_out_means_an_elevated_instance()
    {
        // An elevated instance's pipe admits only Administrators (an elevated token's default owner), a group a normal
        // token holds as deny-only. A pipe for SYSTEM only shuts a normal test run out the same way: Windows refuses
        // the connection before anything is sent.
        Assert.SkipWhen(Elevation.IsElevated, "The test run itself is elevated.");
        var name = UniquePipeName();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        await using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);

        InstancePipeClient.Send(name, InstanceCommand.Exit, TimeSpan.FromSeconds(2)).Should().Be(HandOffResult.InstanceElevated);
    }

    [Fact]
    public void A_pipe_owned_by_the_user_is_a_normal_instance_and_any_launch_of_the_user_may_reach_it()
    {
        InstancePipeClient.IsOwnPipe(User, User, tokenOwner: User).Should().BeTrue();

        // An elevated launch: its token's default owner is Administrators. 1.0.0 refused here ("not owned by the
        // current user"), so --exit from an elevated prompt could not close a normal instance.
        InstancePipeClient.IsOwnPipe(User, User, tokenOwner: Administrators).Should().BeTrue();
    }

    [Fact]
    public void A_pipe_owned_by_Administrators_is_an_elevated_instance_and_only_an_elevated_launch_trusts_it()
    {
        InstancePipeClient.IsOwnPipe(Administrators, User, tokenOwner: Administrators).Should().BeTrue();
        InstancePipeClient.IsOwnPipe(Administrators, User, tokenOwner: User).Should().BeFalse();
    }

    [Fact]
    public void A_pipe_owned_by_anyone_else_is_not_sent_to()
    {
        InstancePipeClient.IsOwnPipe(OtherUser, User, tokenOwner: User).Should().BeFalse();
        InstancePipeClient.IsOwnPipe(OtherUser, User, tokenOwner: Administrators).Should().BeFalse();
        InstancePipeClient.IsOwnPipe(null, User, tokenOwner: User).Should().BeFalse();
    }

    [Fact]
    public void A_refused_connection_means_an_elevated_instance_only_when_this_launch_is_not_elevated()
    {
        var refused = new UnauthorizedAccessException("Access to the path is denied.");

        InstancePipeClient.Classify(refused, thisProcessElevated: false).Should().Be(HandOffResult.InstanceElevated);
        InstancePipeClient.Classify(refused, thisProcessElevated: true).Should().Be(HandOffResult.Failed);
        InstancePipeClient.Classify(new TimeoutException(), thisProcessElevated: false).Should().Be(HandOffResult.NoAnswer);
        InstancePipeClient.Classify(new IOException("Pipe is broken."), thisProcessElevated: false).Should().Be(HandOffResult.Failed);
    }

    private static string UniquePipeName() => $"CashGear.Capture.Tests.{Guid.NewGuid():N}";
}
