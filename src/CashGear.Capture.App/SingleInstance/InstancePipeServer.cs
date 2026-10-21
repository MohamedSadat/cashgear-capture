using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Text;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.Core.Instance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.SingleInstance;

/// <summary>Listens for commands from later launches (one line of UTF-8 per connection) and dispatches them.</summary>
internal sealed partial class InstancePipeServer(
    InstanceCommandDispatcher dispatcher,
    UiReadySignal uiReady,
    ILogger<InstancePipeServer> logger) : BackgroundService
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogListening(InstanceNames.PipeName);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ServeOneAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad client must not kill the listener.
                LogConnectionFailed(ex);
            }
        }
    }

    private async Task ServeOneAsync(CancellationToken ct)
    {
        // CurrentUserOnly admits (and makes owner) the token's default owner: the user, or Administrators when this
        // instance runs elevated. An elevated instance thus refuses launches that aren't elevated, on purpose: it would
        // otherwise take commands from any program of the user's. Those launches say so (HandOffResult.InstanceElevated).
        var server = new NamedPipeServerStream(
            InstanceNames.PipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using (server.ConfigureAwait(false))
        {
            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReadTimeout);
            using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);

            if (!InstanceCommandParser.TryParseWire(line, out var command))
            {
                LogUnknownCommand(line is null ? "<none>" : line[..Math.Min(line.Length, 64)]);
                return;
            }

            LogReceived(command);
            await uiReady.WhenReady.WaitAsync(ct).ConfigureAwait(false);
            _ = DispatchAsync(command); // don't hold up the next connection while a capture runs
        }
    }

    private async Task DispatchAsync(InstanceCommand command)
    {
        try
        {
            await dispatcher.DispatchAsync(command).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogDispatchFailed(ex, command);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening for instance commands on pipe {Pipe}")]
    private partial void LogListening(string pipe);

    [LoggerMessage(Level = LogLevel.Information, Message = "Received instance command {Command}")]
    private partial void LogReceived(InstanceCommand command);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignored unknown instance command {Text}")]
    private partial void LogUnknownCommand([Localizable(false)] string text); // only logged

    [LoggerMessage(Level = LogLevel.Warning, Message = "Instance pipe connection failed")]
    private partial void LogConnectionFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling instance command {Command} failed")]
    private partial void LogDispatchFailed(Exception ex, InstanceCommand command);
}
