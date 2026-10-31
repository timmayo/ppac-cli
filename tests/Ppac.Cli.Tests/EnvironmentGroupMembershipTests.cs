using System.Net;
using System.Text.Json.Nodes;
using Ppac.Cli.Api;
using Ppac.Cli.Commands;
using Ppac.Cli.Config;
using Ppac.Cli.Registry;
using Ppac.Cli.Tests.TestSupport;

namespace Ppac.Cli.Tests;

/// <summary>Group add-environment / remove-environment behavior against a fake service.</summary>
[Collection("GlobalState")]
public sealed class EnvironmentGroupMembershipTests : IDisposable
{
    private static readonly CloudConfig Cloud = CloudConfig.Commercial;

    private readonly TextWriter _previousOut = Console.Out;
    private readonly TextWriter _previousErr = Console.Error;
    private readonly Func<TimeSpan, CancellationToken, Task> _previousDelay = EnvironmentGroupWriteCommands.Delay;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private int _delays;

    public EnvironmentGroupMembershipTests()
    {
        Console.SetOut(_out);
        Console.SetError(_err);
        EnvironmentGroupWriteCommands.Delay = (_, _) =>
        {
            _delays++;
            return Task.CompletedTask;
        };
    }

    public void Dispose()
    {
        Console.SetOut(_previousOut);
        Console.SetError(_previousErr);
        EnvironmentGroupWriteCommands.Delay = _previousDelay;
    }

    /// <summary>
    /// A fake service. The environment is a member when IsMember is true. The POST flips membership once
    /// "visibleAfterPolls" membership checks have happened since it was sent (0 = at once, -1 = never).
    /// </summary>
    private sealed class Service
    {
        public bool IsMember;
        public int VisibleAfterPolls;
        public HttpStatusCode GroupStatus = HttpStatusCode.OK;
        public HttpStatusCode PostStatus = HttpStatusCode.Accepted;
        public string PostBody = "";
        public HttpStatusCode FilterStatus = HttpStatusCode.OK;
        public IDictionary<string, string>? PostHeaders;
        public bool PostSent;
        public bool TargetState;
        /// <summary>Before the POST, decides per membership read (1-based) whether the environment is listed. Simulates inconsistent reads.</summary>
        public Func<int, bool>? ReadScript;
        private int _reads;
        private int _pollsSincePost;

        public FakeHandler Handler => new(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post)
            {
                PostSent = true;
                TargetState = url.Contains("/addEnvironment/");
                _pollsSincePost = 0;
                if (VisibleAfterPolls == 0) IsMember = TargetState;
                return (PostStatus, PostBody, PostHeaders);
            }
            if (url.Contains("/environmentGroups/"))
            {
                return GroupStatus == HttpStatusCode.NoContent
                    ? (HttpStatusCode.NoContent, "", null)
                    : (HttpStatusCode.OK, "{\"id\":\"g1\",\"displayName\":\"Test Zone\"}", null);
            }
            if (url.Contains("/environments?"))
            {
                if (FilterStatus != HttpStatusCode.OK)
                {
                    return (FilterStatus, "{\"title\":\"Bad Request\"}", null);
                }
                if (!PostSent && ReadScript is not null)
                {
                    return (HttpStatusCode.OK, ReadScript(++_reads) ? "{\"value\":[{\"id\":\"e1\"}]}" : "{\"value\":[]}", null);
                }
                if (PostSent && VisibleAfterPolls > 0)
                {
                    _pollsSincePost++;
                    if (_pollsSincePost >= VisibleAfterPolls) IsMember = TargetState;
                }
                return (HttpStatusCode.OK, IsMember ? "{\"value\":[{\"id\":\"e1\"}]}" : "{\"value\":[]}", null);
            }
            return (HttpStatusCode.OK, "{\"displayName\":\"ppac-test2\",\"type\":\"Sandbox\",\"state\":\"Enabled\"}", null);
        });
    }

    private static PowerPlatformApi Api(FakeHandler handler) => new(new HttpClient(handler), "t");

    private static EnvironmentGroupWriteCommands.MembershipChange Change(
        bool remove, bool whatIf = false, bool yes = false, bool noWait = false, int timeout = 30,
        bool json = false, bool interactive = false) =>
        new(remove, "g1", "e1", whatIf, yes, noWait, timeout, json, interactive);

    private Task<int> Run(FakeHandler handler, EnvironmentGroupWriteCommands.MembershipChange change, string answer = "") =>
        EnvironmentGroupWriteCommands.MembershipCoreAsync(Api(handler), Cloud, change, new StringReader(answer), CancellationToken.None);

    private static int Posts(FakeHandler handler) => handler.Requests.Count(r => r.Method == HttpMethod.Post);

    // ---- request shapes ----

    [Fact]
    public void Add_and_remove_requests_post_without_a_body()
    {
        var add = EnvironmentGroupApi.AddEnvironmentRequest(Cloud, "g1", "e 1");
        var remove = EnvironmentGroupApi.RemoveEnvironmentRequest(Cloud, "g1", "e1");

        Assert.Equal(HttpMethod.Post, add.Method);
        Assert.Equal(
            "https://api.powerplatform.com/environmentmanagement/environmentGroups/g1/addEnvironment/e%201?api-version=2024-10-01",
            add.Url);
        Assert.Null(add.Body);
        Assert.EndsWith("/removeEnvironment/e1?api-version=2024-10-01", remove.Url);
    }

    // ---- add ----

    [Fact]
    public async Task Add_whatif_looks_both_up_and_sends_no_post()
    {
        var service = new Service();
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(remove: false, whatIf: true));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(0, Posts(handler));
        var text = _out.ToString();
        Assert.Contains("group 'Test Zone' (g1)", text);
        Assert.Contains("environment 'ppac-test2' (Sandbox, e1)", text);
        Assert.Contains("Current membership: not in the group.", text);
        Assert.Contains("Request: POST https://", text);
    }

    [Fact]
    public async Task Add_waits_until_the_environment_shows_up_in_the_group()
    {
        var service = new Service { VisibleAfterPolls = 3 };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(remove: false));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(1, Posts(handler));
        Assert.True(_delays >= 1);
        Assert.Contains("Added environment 'ppac-test2'", _out.ToString());
    }

    [Fact]
    public async Task Add_json_reports_status_membership_and_operation_location()
    {
        var service = new Service
        {
            PostHeaders = new Dictionary<string, string> { ["Operation-Location"] = "https://example.test/op/1" }
        };

        var exitCode = await Run(service.Handler, Change(remove: false, json: true));

        Assert.Equal(ExitCodes.Success, exitCode);
        var result = JsonNode.Parse(_out.ToString())!;
        Assert.True((bool)result["accepted"]!);
        Assert.Equal(202, (int)result["status"]!);
        Assert.Equal("confirmed", result["membership"]!.ToString());
        Assert.Equal("https://example.test/op/1", result["operationLocation"]!.ToString());
    }

    [Fact]
    public async Task Add_no_wait_returns_after_the_post_without_checking_membership_again()
    {
        var service = new Service { VisibleAfterPolls = -1 };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(remove: false, noWait: true));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(0, _delays);
        Assert.Contains("not checked", _out.ToString());
    }

    [Fact]
    public async Task Add_times_out_with_exit_1_and_a_json_error_when_the_change_never_shows_up()
    {
        var service = new Service { VisibleAfterPolls = -1 };

        var exitCode = await Run(service.Handler, Change(remove: false, timeout: 6, json: true));

        Assert.Equal(ExitCodes.Failure, exitCode);
        var error = JsonNode.Parse(_out.ToString())!["error"]!;
        Assert.Equal("timeout", error["details"]!["membership"]!.ToString());
        Assert.True((bool)error["details"]!["accepted"]!);
        Assert.Contains("not visible after 6 seconds", error["detail"]!.ToString());
    }

    [Fact]
    public async Task Add_when_already_a_member_sends_nothing_and_succeeds()
    {
        var service = new Service { IsMember = true };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(remove: false));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(0, Posts(handler));
        Assert.Contains("already in", _out.ToString());
    }

    [Fact]
    public async Task Add_to_an_unknown_group_exits_4_and_sends_nothing()
    {
        var service = new Service { GroupStatus = HttpStatusCode.NoContent };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(remove: false));

        Assert.Equal(ExitCodes.NotFound, exitCode);
        Assert.Equal(0, Posts(handler));
        Assert.Contains("was not found", _err.ToString());
    }

    [Fact]
    public async Task Add_with_a_membership_filter_the_service_rejects_still_posts_and_reports_unconfirmed()
    {
        var service = new Service { FilterStatus = HttpStatusCode.BadRequest };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(remove: false));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(1, Posts(handler));
        Assert.Contains("unconfirmed", _out.ToString());
    }

    [Fact]
    public async Task Add_a_400_from_the_service_is_an_api_exception_for_the_runner_to_map()
    {
        var service = new Service
        {
            PostStatus = HttpStatusCode.BadRequest,
            PostBody = "{\"detail\":\"Environment is not managed.\"}"
        };
        var failing = service.Handler;

        var ex = await Assert.ThrowsAsync<ApiException>(() => Run(failing, Change(remove: false)));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("Environment is not managed.", ex.Message);
    }

    // ---- remove ----

    [Fact]
    public async Task Remove_prompt_declined_exits_6_and_sends_nothing()
    {
        var service = new Service { IsMember = true };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(remove: true, interactive: true), answer: "n");

        Assert.Equal(ExitCodes.Declined, exitCode);
        Assert.Equal(0, Posts(handler));
        Assert.Contains("Continue? [y/N]", _err.ToString());
        Assert.Contains("Test Zone", _err.ToString());
    }

    [Fact]
    public async Task Remove_with_yes_waits_until_the_environment_is_gone_from_the_group()
    {
        var service = new Service { IsMember = true, VisibleAfterPolls = 2 };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(remove: true, yes: true));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(1, Posts(handler));
        Assert.Contains("/removeEnvironment/", handler.Requests.Single(r => r.Method == HttpMethod.Post).RequestUri!.AbsoluteUri);
        Assert.Contains("Removed environment", _out.ToString());
    }

    [Fact]
    public async Task Remove_of_an_environment_that_is_not_a_member_exits_4_and_sends_nothing()
    {
        var service = new Service { IsMember = false };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(remove: true, yes: true));

        Assert.Equal(ExitCodes.NotFound, exitCode);
        Assert.Equal(0, Posts(handler));
    }

    [Fact]
    public async Task Remove_sends_the_request_when_the_membership_reads_disagree()
    {
        // Live, a member was reported as "not a member" and remove-environment skipped the removal.
        var service = new Service { ReadScript = read => read > 3 };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(remove: true, yes: true));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(1, Posts(handler));
        Assert.Contains("disagreed", _err.ToString());
    }

    [Fact]
    public async Task Add_sends_the_request_when_the_membership_reads_disagree()
    {
        // A stale read can say "member" after a removal; adding must not be skipped on that.
        var service = new Service { ReadScript = read => read <= 3 };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(remove: false));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(1, Posts(handler));
        Assert.Contains("disagreed", _err.ToString());
    }

    [Fact]
    public async Task Whatif_says_the_membership_is_unclear_when_the_reads_disagree()
    {
        var service = new Service { ReadScript = read => read % 2 == 0 };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(remove: true, whatIf: true, json: true));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(0, Posts(handler));
        var result = JsonNode.Parse(_out.ToString())!;
        Assert.Equal("unclear", result["details"]!["membership"]!.ToString());
    }

    [Fact]
    public async Task Remove_whatif_does_not_prompt_and_sends_nothing()
    {
        var service = new Service { IsMember = true };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(remove: true, whatIf: true));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(0, Posts(handler));
        Assert.DoesNotContain("Continue?", _err.ToString());
        Assert.Contains("unlocked", _out.ToString());
    }

    // ---- registry ----

    [Fact]
    public void Membership_commands_are_registered_with_the_right_safety_options()
    {
        var add = CommandRegistry.All.Single(s => s.Name == "environment group add-environment");
        var remove = CommandRegistry.All.Single(s => s.Name == "environment group remove-environment");

        Assert.Equal(CommandClass.Write, add.Class);
        Assert.Equal(CommandClass.Destructive, remove.Class);
        Assert.All(new[] { add, remove }, s => Assert.Equal("EnvironmentManagement.Groups.ReadWrite", s.Permission));
        Assert.All(new[] { add, remove }, s => Assert.False(s.McpEligible, s.Name));
        Assert.DoesNotContain(add.Options, o => o.Name == "--yes");
        Assert.Contains(remove.Options, o => o.Name == "--yes");
        Assert.All(new[] { add, remove }, s => Assert.Contains(s.Options, o => o.Name == "--whatif"));
        Assert.All(new[] { add, remove }, s => Assert.Contains(s.Options, o => o.Name == "--no-wait"));
        Assert.All(new[] { add, remove }, s => Assert.True(s.Options.Single(o => o.Name == "--group").Required));
        Assert.All(new[] { add, remove }, s => Assert.True(s.Options.Single(o => o.Name == "--environment").Required));
    }
}
