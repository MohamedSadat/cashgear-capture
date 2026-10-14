using System.IO;
using System.IO.Pipes;
using System.Text;
using CashGear.Capture.Core.Instance;
using CashGear.Capture.Interop.Native;
using Serilog;

namespace CashGear.Capture.App.SingleInstance;

/// <summary>Used by a second launch (before any host exists) to hand its command to the running instance.</summary>
internal static class InstancePipeClient
{
    public static bool TrySend(string pipeName, InstanceCommand command, TimeSpan timeout)
    {
        try
        {
            // This launch came from the user, so it may hand foreground rights on: the running instance needs them
            // to activate the region overlay for keyboard input.
            User32.AllowSetForegroundWindow(User32.AsfwAny);

            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeout);
            using (var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true))
            {
                writer.WriteLine(InstanceCommandParser.ToWire(command));
            }

            client.WaitForPipeDrain();
            Log.ForContext(typeof(InstancePipeClient)).Information("Forwarded {Command} to the running instance", command);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            Log.ForContext(typeof(InstancePipeClient)).Warning(ex, "Could not reach the running instance on pipe {Pipe}", pipeName);
            return false;
        }
    }
}
