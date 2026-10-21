using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Resources;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Outputs;

/// <summary>What one step did.</summary>
/// <param name="Destination">The step's destination id.</param>
/// <param name="Name">The destination's name (for the summary).</param>
public sealed record StepOutcome(string Destination, string Name, OutputResult Result);

/// <summary>What a run of the workflow did, step by step.</summary>
/// <param name="Pending">The steps after the editor, waiting for its Continue button.</param>
public sealed record WorkflowSummary(IReadOnlyList<StepOutcome> Steps, IReadOnlyList<WorkflowStep> Pending)
{
    public static WorkflowSummary Empty { get; } = new([], []);

    public bool AnyFailed => Steps.Any(s => !s.Result.Succeeded);

    public IEnumerable<string> Links => Steps.Select(s => s.Result.Link).OfType<string>();

    /// <summary>Whether the editor step ran (it shows the capture, so the summary needn't say much).</summary>
    public bool OpenedEditor => Steps.Any(s => DestinationIds.Same(s.Destination, DestinationIds.Editor) && s.Result.Succeeded);

    /// <summary>One line per step that did or tried something: "Office: uploaded", "Word: the document is open in Word".</summary>
    public IEnumerable<string> Lines => Steps
        .Where(s => !s.Result.Skipped && !DestinationIds.Same(s.Destination, DestinationIds.Editor))
        .Select(s => s.Result.Succeeded ? s.Result.Message : OutputsStrings.StepFailed(s.Name, s.Result.Message));
}

/// <summary>
/// Runs a workflow: its switched-on steps in order, each to its destination. A step that fails doesn't stop the
/// others. The editor step ends the run: the steps after it are handed to the editor (<see cref="OutputItem.Pending"/>),
/// which runs them on the edited capture when the user clicks Continue.
/// </summary>
public sealed partial class WorkflowRunner(DestinationRegistry registry, ILogger<WorkflowRunner> logger)
{
    public async Task<WorkflowSummary> RunAsync(OutputItem item, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default, AppSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(steps);

        var enabled = steps.Where(s => s.Enabled).ToList();
        var outcomes = new List<StepOutcome>();
        for (var i = 0; i < enabled.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = enabled[i];
            var destination = registry.Find(step.Destination, settings);
            if (destination is null)
            {
                outcomes.Add(new StepOutcome(step.Destination, step.Destination, OutputResult.Failed(OutputsStrings.DestinationGone(step.Destination))));
                continue;
            }

            if (item.Kind == OutputKind.Video && !destination.SupportsVideo)
            {
                outcomes.Add(new StepOutcome(destination.Id, destination.DisplayName, OutputResult.Skip(OutputsStrings.NoRecordings(destination.DisplayName))));
                continue;
            }

            var isEditor = DestinationIds.Same(destination.Id, DestinationIds.Editor);
            if (isEditor)
            {
                item.Pending = [.. enabled.Skip(i + 1)];
            }

            var result = await SendAsync(destination, item, ct).ConfigureAwait(false);
            outcomes.Add(new StepOutcome(destination.Id, destination.DisplayName, result));
            if (result.Link is { } link)
            {
                item.Links.Add(link);
            }

            if (isEditor && result.Succeeded)
            {
                return new WorkflowSummary(outcomes, item.Pending);
            }

            if (isEditor)
            {
                item.Pending = []; // the editor didn't open: go on with the rest now
            }
        }

        return new WorkflowSummary(outcomes, []);
    }

    private async Task<OutputResult> SendAsync(IOutputDestination destination, OutputItem item, CancellationToken ct)
    {
        try
        {
            var result = await destination.SendAsync(item, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                LogStepFailed(destination.Id, result.Message);
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One destination's surprise mustn't keep the capture from the others.
            LogStepThrew(ex, destination.Id);
            return OutputResult.Failed(ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Output step {Destination} failed: {Message}")]
    private partial void LogStepFailed(string destination, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Output step {Destination} threw")]
    private partial void LogStepThrew(Exception ex, string destination);
}
