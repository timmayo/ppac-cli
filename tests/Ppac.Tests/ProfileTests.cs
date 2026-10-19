using Ppac.Core;
using Ppac.Core.Config;

namespace Ppac.Tests;

public class ProfileTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "ppac-profiles-" + Guid.NewGuid().ToString("N"));

    private static Profile P(string name, string tenant = "t", string client = "c", string? user = null) =>
        new(tenant, client, user, ProfileMethods.Interactive, null, name);

    private static ProfileStore Two()
    {
        var store = new ProfileStore(TempDir());
        store.Upsert(P("alpha", "t1", "c1", "a@x.com"), makeActive: true);
        store.Upsert(P("beta", "t2", "c2", "b@x.com"), makeActive: false);
        return store;
    }

    [Fact]
    public void Upsert_adds_profiles_in_order_and_activates_when_asked()
    {
        var file = Two().LoadAll();
        Assert.Equal(2, file.Profiles.Count);
        Assert.Equal("alpha", file.Profiles[0].Name);
        Assert.Equal("beta", file.Profiles[1].Name);
        Assert.Equal("alpha", file.Active);
    }

    [Fact]
    public void Upsert_with_the_same_name_ignoring_case_replaces_instead_of_adding()
    {
        var store = Two();
        store.Upsert(P("ALPHA", "t9", "c9"), makeActive: false);

        var file = store.LoadAll();
        Assert.Equal(2, file.Profiles.Count);
        Assert.Equal("t9", file.Profiles[0].TenantId);
        Assert.Equal("ALPHA", file.Profiles[0].Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this-profile-name-is-way-too-long-for-pac")]
    public void Invalid_names_are_usage_errors(string name)
    {
        var ex = Assert.Throws<PpacException>(() => ProfileStore.ValidateName(name));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
    }

    [Fact]
    public void A_name_of_exactly_30_characters_is_accepted()
    {
        Assert.Equal(30, ProfileStore.ValidateName(new string('a', 30)).Length);
    }

    [Fact]
    public void Find_by_index_is_one_based_and_out_of_range_is_not_found()
    {
        var file = Two().LoadAll();
        Assert.Equal("alpha", ProfileStore.Find(file, null, 1).Name);
        Assert.Equal("beta", ProfileStore.Find(file, null, 2).Name);
        Assert.Equal(ExitCodes.NotFound, Assert.Throws<PpacException>(() => ProfileStore.Find(file, null, 3)).ExitCode);
    }

    [Fact]
    public void Find_by_name_ignores_case_and_unknown_is_not_found()
    {
        var file = Two().LoadAll();
        Assert.Equal("beta", ProfileStore.Find(file, "BETA", 0).Name);
        Assert.Equal(ExitCodes.NotFound, Assert.Throws<PpacException>(() => ProfileStore.Find(file, "nope", 0)).ExitCode);
    }

    [Fact]
    public void Find_needs_exactly_one_of_index_and_name()
    {
        var file = Two().LoadAll();
        Assert.Equal(ExitCodes.Usage, Assert.Throws<PpacException>(() => ProfileStore.Find(file, null, 0)).ExitCode);
        Assert.Equal(ExitCodes.Usage, Assert.Throws<PpacException>(() => ProfileStore.Find(file, "alpha", 1)).ExitCode);
    }

    [Fact]
    public void Select_changes_the_active_profile()
    {
        var store = Two();
        store.Select("beta", 0);
        Assert.Equal("beta", store.LoadAll().Active);
        Assert.Equal("beta", store.Load()!.Name);
    }

    [Fact]
    public void Deleting_the_active_profile_leaves_none_active_and_deleting_another_keeps_it()
    {
        var store = Two();
        store.Delete(null, 2);
        Assert.Equal("alpha", store.LoadAll().Active);

        store.Delete("alpha", 0);
        var file = store.LoadAll();
        Assert.Empty(file.Profiles);
        Assert.True(file.Active is null);
        Assert.True(store.Load() is null);
    }

    [Fact]
    public void Rename_updates_the_active_pointer_and_rejects_a_name_in_use()
    {
        var store = Two();
        store.Rename(1, "gamma");
        var file = store.LoadAll();
        Assert.Equal("gamma", file.Profiles[0].Name);
        Assert.Equal("gamma", file.Active);

        var ex = Assert.Throws<PpacException>(() => store.Rename(1, "beta"));
        Assert.Equal(ExitCodes.Conflict, ex.ExitCode);
    }

    [Fact]
    public void Rename_to_the_same_name_with_different_case_is_allowed()
    {
        var store = Two();
        store.Rename(1, "ALPHA");
        Assert.Equal("ALPHA", store.LoadAll().Profiles[0].Name);
    }

    [Fact]
    public void Clear_removes_every_profile()
    {
        var store = Two();
        store.Clear();
        Assert.Empty(store.LoadAll().Profiles);
    }

    [Fact]
    public void OtherProfileUsingAccount_finds_a_profile_with_the_same_user_and_tenant_only()
    {
        var store = new ProfileStore(TempDir());
        store.Upsert(P("one", "t1", "c1", "me@x.com"), makeActive: true);
        store.Upsert(P("two", "t1", "c2", "ME@x.com"), makeActive: false);
        store.Upsert(P("other-tenant", "t2", "c1", "me@x.com"), makeActive: false);
        store.Upsert(P("signed-out", "t1", "c1", null), makeActive: false);
        var file = store.LoadAll();

        Assert.Equal("two", ProfileStore.OtherProfileUsingAccount(file, file.Profiles[0])!.Name);
        Assert.Equal("one", ProfileStore.OtherProfileUsingAccount(file, file.Profiles[1])!.Name);
        Assert.True(ProfileStore.OtherProfileUsingAccount(file, file.Profiles[2]) is null);
        Assert.True(ProfileStore.OtherProfileUsingAccount(file, file.Profiles[3]) is null);
    }

    [Fact]
    public void A_profile_json_from_before_named_profiles_is_read_as_default()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "profile.json"),
            """{"TenantId":"old-tenant","ClientId":"old-client","Username":"me@x.com"}""");

        var store = new ProfileStore(dir);
        var file = store.LoadAll();

        Assert.Single(file.Profiles);
        Assert.Equal("default", file.Active);
        Assert.Equal("old-tenant", file.Profiles[0].TenantId);
        Assert.Equal("me@x.com", file.Profiles[0].Username);
        Assert.Equal("old-tenant", store.Resolve(null, null, _ => null).TenantId);
    }

    [Fact]
    public void Resolve_uses_the_named_profile_before_the_active_one()
    {
        var store = Two();
        Assert.Equal("t2", store.Resolve(null, null, _ => null, "beta").TenantId);
        Assert.Equal("t1", store.Resolve(null, null, _ => null).TenantId);
    }

    [Fact]
    public void Resolve_reads_PPAC_PROFILE_and_an_unknown_profile_is_not_found()
    {
        var store = Two();
        var env = new Dictionary<string, string?> { ["PPAC_PROFILE"] = "beta" };
        Assert.Equal("c2", store.Resolve(null, null, k => env.GetValueOrDefault(k)).ClientId);

        var ex = Assert.Throws<PpacException>(() => store.Resolve(null, null, _ => null, "nope"));
        Assert.Equal(ExitCodes.NotFound, ex.ExitCode);
    }

    [Fact]
    public void ResolveForCreate_reuses_an_existing_profile_of_that_name_but_never_copies_another()
    {
        var store = Two();

        var refresh = store.ResolveForCreate("beta", null, null, _ => null);
        Assert.Equal("t2", refresh.TenantId);
        Assert.Equal("c2", refresh.ClientId);
        Assert.Equal("beta", refresh.Name);

        var ex = Assert.Throws<PpacException>(() => store.ResolveForCreate("fresh", null, null, _ => null));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Contains("--tenant", ex.Message);
    }

    [Fact]
    public void ResolveForCreate_defaults_the_name_and_takes_flags_first()
    {
        var store = new ProfileStore(TempDir());
        var profile = store.ResolveForCreate(null, "flag-t", "flag-c", _ => null);
        Assert.Equal("default", profile.Name);
        Assert.Equal("flag-t", profile.TenantId);
        Assert.Equal("flag-c", profile.ClientId);
    }
}