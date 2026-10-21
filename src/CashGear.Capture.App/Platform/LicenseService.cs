using System.IO;
using System.Net.Http;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Licensing;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Platform;

/// <summary>
/// This PC's licence (<c>license.json</c> in the data folder) and the features it unlocks. In this version every
/// feature is free (<see cref="FeaturePolicy.AllFree"/>), so the app asks for no licence and shows no licence UI;
/// what is here is what a sold version needs: reading and activating a licence file, refreshing the activation,
/// and the gate the Pro features ask (the video editor's annotations, speed and effects, zooms, captions, GIFs).
/// </summary>
public sealed partial class LicenseService : IFeatureGate
{
    private readonly AppPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<LicenseService> _logger;
    private readonly LicenseEvaluator? _evaluator;
    private readonly string _machineId;
    private readonly Func<HttpClient> _http;

    public LicenseService(AppPaths paths, TimeProvider time, ILogger<LicenseService> logger)
        : this(paths, time, logger, BuildInfo.LicensePublicKey, FeaturePolicy.AllFree, MachineId.ForThisPc(), () => new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
    {
    }

    internal LicenseService(
        AppPaths paths, TimeProvider time, ILogger<LicenseService> logger, string? publicKey, FeaturePolicy policy, string machineId, Func<HttpClient> http)
    {
        _paths = paths;
        _time = time;
        _logger = logger;
        _evaluator = publicKey is null ? null : new LicenseEvaluator(publicKey);
        Policy = policy;
        _machineId = machineId;
        _http = http;
    }

    public FeaturePolicy Policy { get; }

    /// <summary>Where the licence and its activation are kept.</summary>
    public string File => Path.Combine(_paths.DataDirectory, "license.json");

    /// <summary>The licence as it stands now (read each time: it is small).</summary>
    public LicenseStatus Status =>
        _evaluator is null ? LicenseStatus.Free : _evaluator.Evaluate(Load(), _machineId, _time.GetUtcNow());

    /// <summary>What Settings › General says about the licence.</summary>
    public string Summary => Policy.IsAllFree ? CommonText.AllFeaturesIncluded : Status.Edition == Edition.Pro ? CommonText.ProEdition : CommonText.FreeEdition;

    public bool IsEnabled(ProFeature feature) => Policy.IsEnabled(feature, Status);

    /// <summary>
    /// Takes a licence file and activates it on this PC at the licence's endpoint. Throws
    /// <see cref="ActivationRefusedException"/> or <see cref="HttpRequestException"/>; a file that isn't a genuine
    /// licence throws <see cref="InvalidDataException"/>.
    /// </summary>
    public async Task<LicenseStatus> ActivateAsync(string licenseText, CancellationToken ct = default)
    {
        var evaluator = _evaluator ?? throw new InvalidOperationException("This build has no licence key.");
        var (license, state) = evaluator.ReadLicense(licenseText, _time.GetUtcNow());
        if (license is null || state != LicenseState.Active)
        {
            throw new InvalidDataException(state.ToString());
        }

        var token = license.ActivationUrl is { } url
            ? await new ActivationClient(_http()).ActivateAsync(new Uri(url), licenseText, _machineId, ct).ConfigureAwait(false)
            : null;
        Save(new StoredLicense(licenseText, token, token is null ? null : _time.GetUtcNow()));
        LogActivated(license.Id);
        return Status;
    }

    /// <summary>
    /// Asks the endpoint to confirm the activation once its refresh date has passed; an endpoint that can't be
    /// reached leaves it as it was (the grace period runs). A refusal (revoked, for example) removes the activation.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_evaluator is null || Load() is not { Token: not null } stored || Status.State is not (LicenseState.Grace or LicenseState.GraceOver)
            || Status.License?.ActivationUrl is not { } url)
        {
            return;
        }

        try
        {
            var token = await new ActivationClient(_http()).ActivateAsync(new Uri(url), stored.License, _machineId, ct).ConfigureAwait(false);
            Save(stored with { Token = token, LastChecked = _time.GetUtcNow() });
        }
        catch (ActivationRefusedException ex)
        {
            LogRefused(ex.Error);
            Save(stored with { Token = null });
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(ex);
        }
    }

    private StoredLicense? Load()
    {
        try
        {
            return System.IO.File.Exists(File) ? StoredLicense.TryParse(System.IO.File.ReadAllText(File)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnreadable(ex);
            return null;
        }
    }

    private void Save(StoredLicense stored)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, stored.ToText());
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Licence {Id} activated on this PC")]
    private partial void LogActivated(string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The activation endpoint refused the licence: {Error}")]
    private partial void LogRefused(string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "The activation endpoint can't be reached; the grace period runs")]
    private partial void LogUnreachable(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The licence file can't be read")]
    private partial void LogUnreadable(Exception ex);
}
