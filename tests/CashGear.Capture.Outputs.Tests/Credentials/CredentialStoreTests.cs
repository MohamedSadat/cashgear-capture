using System.Text;
using System.Text.Json.Nodes;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Credentials;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Outputs.Tests.Credentials;

public sealed class CredentialStoreTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private DpapiCredentialStore Store() => new(_temp.AppPaths, NullLogger<DpapiCredentialStore>.Instance);

    [Fact]
    public void Secrets_survive_a_restart_and_are_never_in_the_file_as_text()
    {
        const string password = "correct horse battery staple";
        Store().Set("server:office:password", password);

        var store = Store();
        store.Get("server:office:password").Should().Be(password);
        store.Get("server:office:passphrase").Should().BeNull();
        store.FilePath.Should().Be(Path.Combine(_temp.AppPaths.DataDirectory, "secrets.json"));
        var file = File.ReadAllText(store.FilePath);
        file.Should().Contain("server:office:password", "keys are not secret")
            .And.NotContain(password)
            .And.NotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(password)))
            .And.NotContain(Convert.ToBase64String(Encoding.Unicode.GetBytes(password)));
    }

    [Fact]
    public void A_secret_copied_to_another_key_cant_be_read()
    {
        var store = Store();
        store.Set("upload:a:token", "Bearer a");
        var json = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
        json["upload:b:token"] = json["upload:a:token"]!.GetValue<string>();
        File.WriteAllText(store.FilePath, json.ToJsonString());

        var reread = Store();
        reread.Get("upload:a:token").Should().Be("Bearer a");
        reread.Get("upload:b:token").Should().BeNull("each secret is bound to its key");
    }

    [Fact]
    public void A_damaged_file_means_no_secrets_and_is_replaced_on_the_next_change()
    {
        Directory.CreateDirectory(_temp.AppPaths.DataDirectory);
        File.WriteAllText(Path.Combine(_temp.AppPaths.DataDirectory, "secrets.json"), "{ not json");

        var store = Store();
        store.Get("server:a:password").Should().BeNull();
        store.Set("server:a:password", "p");

        Store().Get("server:a:password").Should().Be("p");
    }

    [Fact]
    public void A_blob_that_isnt_dpapi_reads_as_missing()
    {
        Directory.CreateDirectory(_temp.AppPaths.DataDirectory);
        File.WriteAllText(Path.Combine(_temp.AppPaths.DataDirectory, "secrets.json"), """{ "server:a:password": "bm90IGVuY3J5cHRlZA==", "server:b:password": "%%%" }""");

        var store = Store();

        store.Get("server:a:password").Should().BeNull();
        store.Get("server:b:password").Should().BeNull();
    }

    [Fact]
    public void Empty_removes_and_a_deleted_destination_takes_its_secrets_along()
    {
        var store = Store();
        var office = new ServerDestination("office");
        var backup = new ServerDestination("backup");
        store.Set(CredentialKeys.Password(office), "1");
        store.Set(CredentialKeys.Passphrase(office), "2");
        store.Set(CredentialKeys.Password(backup), "3");
        store.Set(CredentialKeys.Token(new HttpDestination("office")), "4");

        store.Set(CredentialKeys.Passphrase(office), string.Empty);
        store.Get(CredentialKeys.Passphrase(office)).Should().BeNull();
        store.RemoveAll(CredentialKeys.Prefix(office));

        var reread = Store();
        reread.Get(CredentialKeys.Password(office)).Should().BeNull();
        reread.Get(CredentialKeys.Password(backup)).Should().Be("3");
        reread.Get(CredentialKeys.Token(new HttpDestination("office"))).Should().Be("4", "an upload with the same id is another destination");
    }

    [Fact]
    public void Typed_secrets_wait_for_commit()
    {
        var stored = Store();
        stored.Set("server:a:password", "old");
        stored.Set("server:gone:password", "x");
        var pending = new PendingCredentials(stored);

        pending.Set("server:a:password", "new");
        pending.RemoveAll("server:gone:");
        pending.Set("server:b:password", "typed");

        pending.Get("server:a:password").Should().Be("new", "Test uses what is typed");
        pending.Has("server:gone:password").Should().BeFalse();
        pending.HasChanges.Should().BeTrue();
        Store().Get("server:a:password").Should().Be("old", "nothing is stored before OK or Apply");

        pending.Commit();

        var reread = Store();
        reread.Get("server:a:password").Should().Be("new");
        reread.Get("server:b:password").Should().Be("typed");
        reread.Get("server:gone:password").Should().BeNull();
        pending.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void A_destination_deleted_and_made_again_keeps_the_new_secret()
    {
        var stored = Store();
        stored.Set("server:a:password", "old");
        var pending = new PendingCredentials(stored);

        pending.RemoveAll("server:a:");
        pending.Set("server:a:password", "new");
        pending.Commit();

        Store().Get("server:a:password").Should().Be("new");
    }
}
