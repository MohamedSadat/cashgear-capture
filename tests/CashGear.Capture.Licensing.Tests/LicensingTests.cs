using System.Net;
using System.Text;
using CashGear.Capture.Licensing.Tool;

namespace CashGear.Capture.Licensing.Tests;

public sealed class LicensingTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private const string ThisPc = "pc-one";

    private readonly (string Private, string Public) _keys = Signing.NewKeyPair();
    private readonly string _store = Path.Combine(Path.GetTempPath(), "cashgear-tests", "activations-" + Guid.NewGuid().ToString("N")[..8] + ".json");

    public void Dispose() => File.Delete(_store);

    private LicenseInfo License(int machines = 2, DateTimeOffset? expires = null, IReadOnlyList<ProFeature>? features = null, string product = LicenseInfo.ProductName) =>
        new("lic-1", product, "Ada Lovelace", Edition.Pro, features, machines, Now.AddDays(-1), expires, "http://127.0.0.1:1/");

    private string Issue(LicenseInfo license, string? key = null) => Signing.Sign(license.ToBytes(), key ?? _keys.Private).ToText();

    private string Token(DateTimeOffset issued, TimeSpan refresh, string machine = ThisPc, string license = "lic-1") =>
        Signing.Sign(new ActivationToken(license, machine, issued, issued + refresh).ToBytes(), _keys.Private).ToText();

    [Fact]
    public void A_licence_signed_with_the_key_reads_back_and_any_change_breaks_it()
    {
        var evaluator = new LicenseEvaluator(_keys.Public);
        var text = Issue(License());

        var (license, state) = evaluator.ReadLicense(text, Now);
        state.Should().Be(LicenseState.Active);
        license!.Licensee.Should().Be("Ada Lovelace");
        Signing.PublicKeyOf(_keys.Private).Should().Be(_keys.Public);

        var document = SignedDocument.TryParse(text)!;
        var forged = License(machines: 1000).ToBytes();
        evaluator.ReadLicense(new SignedDocument(Convert.ToBase64String(forged), document.Signature).ToText(), Now).License.Should().BeNull("the content changed");
        evaluator.ReadLicense(Issue(License(), Signing.NewKeyPair().PrivateKey), Now).Should().Be((null, LicenseState.Invalid), "another seller's key");
        evaluator.ReadLicense(Issue(License(product: "Another App")), Now).Should().Be((null, LicenseState.Invalid));
        evaluator.ReadLicense(Issue(License(machines: 0)), Now).State.Should().Be(LicenseState.Invalid);
        evaluator.ReadLicense("not a licence", Now).Should().Be((null, LicenseState.Invalid));
        evaluator.ReadLicense(Issue(License(expires: Now.AddDays(-1))), Now).State.Should().Be(LicenseState.Expired);
    }

    [Fact]
    public void A_licence_works_once_activated_on_this_PC_and_through_a_grace_period_offline()
    {
        var evaluator = new LicenseEvaluator(_keys.Public);
        var license = Issue(License());
        var week = TimeSpan.FromDays(7);

        evaluator.Evaluate(null, ThisPc, Now).Should().Be(LicenseStatus.Free);
        evaluator.Evaluate(new StoredLicense(license, null, null), ThisPc, Now).State.Should().Be(LicenseState.NotActivated);
        evaluator.Evaluate(new StoredLicense(license, Token(Now, week, machine: "pc-two"), Now), ThisPc, Now).State.Should().Be(LicenseState.Invalid, "activated for another PC");
        evaluator.Evaluate(new StoredLicense(license, Token(Now, week, license: "lic-other"), Now), ThisPc, Now).State.Should().Be(LicenseState.Invalid);

        var stored = new StoredLicense(license, Token(Now, week), Now);
        var active = evaluator.Evaluate(stored, ThisPc, Now.AddDays(6));
        active.Should().BeEquivalentTo(new { Edition = Edition.Pro, State = LicenseState.Active });

        var grace = evaluator.Evaluate(stored, ThisPc, Now.AddDays(10));
        grace.Edition.Should().Be(Edition.Pro);
        grace.State.Should().Be(LicenseState.Grace);
        grace.GraceUntil.Should().Be(Now.AddDays(7 + 14));

        var over = evaluator.Evaluate(stored, ThisPc, Now.AddDays(22));
        over.Edition.Should().Be(Edition.Free);
        over.State.Should().Be(LicenseState.GraceOver);

        var refreshed = stored with { Token = Token(Now.AddDays(22), week), LastChecked = Now.AddDays(22) };
        evaluator.Evaluate(refreshed, ThisPc, Now.AddDays(22)).State.Should().Be(LicenseState.Active, "a later check brings it back");
    }

    [Fact]
    public void Features_are_free_in_this_version_and_need_Pro_once_it_is_sold()
    {
        var free = LicenseStatus.Free;
        var pro = new LicenseStatus(Edition.Pro, License(), LicenseState.Active);
        var some = new LicenseStatus(Edition.Pro, License(features: [ProFeature.Captions]), LicenseState.Active);

        Enum.GetValues<ProFeature>().Should().OnlyContain(f => FeaturePolicy.AllFree.IsEnabled(f, free));
        Enum.GetValues<ProFeature>().Should().OnlyContain(f => !FeaturePolicy.ProSplit.IsEnabled(f, free));
        Enum.GetValues<ProFeature>().Should().OnlyContain(f => FeaturePolicy.ProSplit.IsEnabled(f, pro));
        FeaturePolicy.ProSplit.IsEnabled(ProFeature.Captions, some).Should().BeTrue();
        FeaturePolicy.ProSplit.IsEnabled(ProFeature.AutoZoom, some).Should().BeFalse();
    }

    [Fact]
    public void A_machine_id_is_a_salted_hash_of_the_machine_guid()
    {
        var id = MachineId.From("1b2c3d4e-0000-1111-2222-333344445555");

        id.Should().MatchRegex("^[0-9a-f]{64}$").And.NotContain("1b2c3d4e");
        MachineId.From(" 1B2C3D4E-0000-1111-2222-333344445555 ").Should().Be(id);
        MachineId.From("ffffffff-0000-1111-2222-333344445555").Should().NotBe(id);
        MachineId.ForThisPc().Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task Activation_binds_a_licence_to_as_many_PCs_as_it_allows()
    {
        var time = new ManualTime(Now);
        using var server = new ActivationServer(Prefix(out var url), _keys.Private, _store, TimeSpan.FromDays(7), time);
        server.Start();
        using var http = new HttpClient();
        var client = new ActivationClient(http);
        var evaluator = new LicenseEvaluator(_keys.Public);
        var license = Issue(License(machines: 2));
        var info = evaluator.ReadLicense(license, Now).License!;

        var first = await client.ActivateAsync(url, license, "pc-one", TestContext.Current.CancellationToken);
        var token = evaluator.ReadToken(first, info, "pc-one");
        token.Should().NotBeNull();
        token!.RefreshAfter.Should().Be(Now.AddDays(7));
        evaluator.ReadToken(first, info, "pc-two").Should().BeNull("a token works on its own PC only");

        await client.ActivateAsync(url, license, "pc-two", TestContext.Current.CancellationToken);
        var third = () => client.ActivateAsync(url, license, "pc-three", TestContext.Current.CancellationToken);
        (await third.Should().ThrowAsync<ActivationRefusedException>()).Which.Error.Should().Be(ActivationErrors.MachineLimit);

        time.Advance(TimeSpan.FromDays(8));
        var refreshed = evaluator.ReadToken(await client.ActivateAsync(url, license, "pc-one", TestContext.Current.CancellationToken), info, "pc-one");
        refreshed!.RefreshAfter.Should().Be(Now.AddDays(15), "a PC already on the list is refreshed");
        server.Machines("lic-1").Should().Equal("pc-one", "pc-two");

        server.Revoke("lic-1");
        var revoked = () => client.ActivateAsync(url, license, "pc-one", TestContext.Current.CancellationToken);
        (await revoked.Should().ThrowAsync<ActivationRefusedException>()).Which.Error.Should().Be(ActivationErrors.Revoked);

        var forged = () => client.ActivateAsync(url, Issue(License(), Signing.NewKeyPair().PrivateKey), "pc-one", TestContext.Current.CancellationToken);
        (await forged.Should().ThrowAsync<ActivationRefusedException>()).Which.Error.Should().Be(ActivationErrors.InvalidLicense);

        var expired = () => client.ActivateAsync(url, Issue(License(expires: Now.AddDays(1)) with { Id = "lic-2" }), "pc-one", TestContext.Current.CancellationToken);
        (await expired.Should().ThrowAsync<ActivationRefusedException>()).Which.Error.Should().Be(ActivationErrors.Expired);
    }

    [Fact]
    public async Task An_endpoint_that_cannot_be_reached_is_a_network_error_not_a_refusal()
    {
        Prefix(out var url); // a free port nobody listens on
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var activate = () => new ActivationClient(http).ActivateAsync(url, Issue(License()), ThisPc, TestContext.Current.CancellationToken);

        await activate.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public void The_server_refuses_what_is_not_a_request()
    {
        using var server = new ActivationServer("http://127.0.0.1:1/", _keys.Private, _store, TimeSpan.FromDays(7));

        server.Handle("{}").Status.Should().Be(HttpStatusCode.Forbidden);
        server.Handle("nonsense").Body.Should().Contain(ActivationErrors.InvalidLicense);
    }

    private static string Prefix(out Uri url)
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        url = new Uri($"http://127.0.0.1:{port}/");
        return url.AbsoluteUri;
    }
}

/// <summary>A clock the test moves.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
