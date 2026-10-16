using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Instance;
using CashGear.Capture.Interop.Native;
using Serilog;

namespace CashGear.Capture.App.SingleInstance;

/// <summary>How handing a command to the running instance ended.</summary>
internal enum HandOffResult
{
    Delivered,

    /// <summary>Nothing answered on the pipe in time.</summary>
    NoAnswer,

    /// <summary>
    /// Windows refused the connection because the running instance is elevated and this process isn't. Its pipe admits
    /// Administrators only, and that is kept on purpose: otherwise any program of the user's could drive an elevated
    /// process.
    /// </summary>
    InstanceElevated,

    Failed,
}

/// <summary>Used by a second launch (before any host exists) to hand its command to the running instance.</summary>
internal static class InstancePipeClient
{
    public static HandOffResult Send(string pipeName, InstanceCommand command, TimeSpan timeout)
    {
        var log = Log.ForContext(typeof(InstancePipeClient));
        try
        {
            // This launch came from the user, so it may hand foreground rights on: the running instance needs them
            // to activate the region overlay for keyboard input.
            User32.AllowSetForegroundWindow(User32.AsfwAny);

            // Not PipeOptions.CurrentUserOnly: .NET then requires the pipe's owner to be this token's default owner, which
            // is Administrators when elevated, so an elevated launch could not reach a normal instance (IsOwnPipe checks
            // instead). Identification: the instance can see who connected but can't act as this process.
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.None, TokenImpersonationLevel.Identification);
            client.Connect(timeout);

            var owner = client.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            using (var identity = WindowsIdentity.GetCurrent())
            {
                if (!IsOwnPipe(owner, identity.User, identity.Owner))
                {
                    log.Warning("Pipe {Pipe} is owned by {Owner}, not by this user; {Command} was not sent", pipeName, owner?.Value, command);
                    return HandOffResult.Failed;
                }
            }

            using (var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true))
            {
                writer.WriteLine(InstanceCommandParser.ToWire(command));
            }

            client.WaitForPipeDrain();
            log.Information("Forwarded {Command} to the running instance", command);
            return HandOffResult.Delivered;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            var result = Classify(ex, Elevation.IsElevated);
            if (result == HandOffResult.InstanceElevated)
            {
                log.Warning(ex, "The running instance refused this launch on pipe {Pipe}: it runs as administrator and this launch doesn't", pipeName);
            }
            else
            {
                log.Warning(ex, "Could not reach the running instance on pipe {Pipe}", pipeName);
            }

            return result;
        }
    }

    /// <summary>
    /// Whether a pipe with this owner is this user's own instance. The server's <see cref="PipeOptions.CurrentUserOnly"/>
    /// makes the token's default owner the pipe's owner: the user for a normal instance, Administrators for an elevated
    /// one. A normal instance is fine to reach from either kind of launch; an elevated one only admits elevated launches.
    /// </summary>
    internal static bool IsOwnPipe(SecurityIdentifier? pipeOwner, SecurityIdentifier? user, SecurityIdentifier? tokenOwner) =>
        pipeOwner is not null && (pipeOwner == user || pipeOwner == tokenOwner);

    internal static HandOffResult Classify(Exception ex, bool thisProcessElevated) => ex switch
    {
        TimeoutException => HandOffResult.NoAnswer,

        // The pipe name carries this user's SID and session, and a normal instance's pipe admits this user. So when a
        // normal launch is refused, the instance is elevated.
        UnauthorizedAccessException when !thisProcessElevated => HandOffResult.InstanceElevated,

        _ => HandOffResult.Failed,
    };
}
