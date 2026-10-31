using System.Net;
using System.Text.Json.Nodes;
using Ppac.Cli.Api;
using Ppac.Cli.Commands;
using Ppac.Cli.Config;
using Ppac.Cli.Registry;
using Ppac.Cli.Tests.TestSupport;

namespace Ppac.Cli.Tests;

/// <summary>'environment managed enable | disable' behavior against a fake service.</summary>
[Collection("GlobalState")]
public sealed class EnvironmentManagedCommandTests : IDisposable
{
    private static readonly CloudConfig Cloud = CloudConfig.Commercial;

    private readonly TextWriter _previousOut = Console.Out;
    private readonly TextWriter _previousErr = Console.Error;
    private readonly Func<TimeSpan, CancellationToken, Task> _previousDelay = EnvironmentManagedCommands.Delay;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private int _delays;

    public EnvironmentManagedCommandTests()
    {
        Console.SetOut(_out);
        Console.SetError(_err);
        EnvironmentManagedCommands.Delay = (_, _) =>
        {
            _delays++;
            return Task.CompletedTask;
        };
    }

    public void Dispose()
    {
        Console.SetOut(_previousOut);
        Console.SetError(_previousErr);
        EnvironmentManagedCommands.Delay = _previousDelay;
    }

    /// <summary>
    /// A fake service. Level is the environment's protectionLevel (null = the property is missing).
    /// An enable/disable POST changes Level after "VisibleAfterPolls" environment reads (0 = at once, -1 = never).
    /// </summary>
    private sealed class Service
    {
        public string? Level = "Basic";
        public int VisibleAfterPolls;
        public HttpStatusCode PostStatus = HttpStatusCode.Accepted;
        public string PostBody = "";
        public HttpStatusCode GetStatus = HttpStatusCode.OK;
        public bool PostSent;
        /// <summary>Before the POST, decides per environment read (1-based) which protectionLevel is returned. Simulates inconsistent reads.</summary>
        public Func<int, string?>? ReadScript;
        private int _reads;
        private string _target = "Basic";
        private int _polls;

        public FakeHandler Handler => new(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post)
            {
                PostSent = true;
                _target = url.Contains("/enableManaged") ? "Standard" : "Basic";
                _polls = 0;
                if (VisibleAfterPolls == 0 && (int)PostStatus < 300) Level = _target;
                return (PostStatus, PostBody, null);
            }

            if (GetStatus != HttpStatusCode.OK)
            {
                return (GetStatus, "", null);
            }
            if (!PostSent && ReadScript is not null)
            {
                var scripted = ReadScript(++_reads);
                return (HttpStatusCode.OK, scripted is null
                    ? "{\"displayName\":\"ppac-test\",\"type\":\"Sandbox\"}"
                    : $"{{\"displayName\":\"ppac-test\",\"type\":\"Sandbox\",\"protectionLevel\":\"{scripted}\"}}", null);
            }
            if (PostSent && VisibleAfterPolls > 0)
            {
                _polls++;
                if (_polls >= VisibleAfterPolls) Level = _target;
            }
            return (HttpStatusCode.OK, Level is null
                ? "{\"displayName\":\"ppac-test\",\"type\":\"Sandbox\"}"
                : $"{{\"displayName\":\"ppac-test\",\"type\":\"Sandbox\",\"protectionLevel\":\"{Level}\"}}", null);
        });
    }

    private static PowerPlatformApi Api(FakeHandler handler) => new(new HttpClient(handler), "t");

    private static EnvironmentManagedCommands.ManagedChange Change(
        bool disable, bool whatIf = false, bool yes = false, bool noWait = false,
        int timeout = 30, bool json = false, bool interactive = false) =>
        new(disable, "e1", whatIf, yes, noWait, timeout, json, interactive);

    private Task<int> Run(FakeHandler handler, EnvironmentManagedCommands.ManagedChange change, string answer = "") =>
        EnvironmentManagedCommands.RunCoreAsync(Api(handler), Cloud, change, new StringReader(answer), CancellationToken.None);

    private static int Posts(FakeHandler handler) => handler.Requests.Count(r => r.Method == HttpMethod.Post);

    // ---- request shapes and state ----

    [Fact]
    public void Enable_and_disable_requests_post_without_a_body()
    {
        var enable = EnvironmentManagedApi.EnableRequest(Cloud, "e 1");
        var disable = EnvironmentManagedApi.DisableRequest(Cloud, "e1");

        Assert.Equal(HttpMethod.Post, enable.Method);
        Assert.Equal(
            "https://api.powerplatform.com/environmentmanagement/environments/e%201/governanceSetting/enableManaged?api-version=2024-10-01",
            enable.Url);
        Assert.Null(enable.Body);
        Assert.Equal(
            "https://api.powerplatform.com/environmentmanagement/environments/e1/governanceSetting/disableManaged?api-version=2024-10-01",
            disable.Url);
    }

    [Theory]
    [InlineData("Basic", false)]
    [InlineData("basic", false)]
    [InlineData("Standard", true)]
    [InlineData("Premium", true)]
    public void Managed_means_a_protection_level_other_than_basic(string level, bool expected) =>
        Assert.Equal(expected, EnvironmentManagedApi.IsManaged(new JsonObject { ["protectionLevel"] = level }));

    [Fact]
    public void Managed_is_unknown_without_a_protection_level()
    {
        Assert.Null(EnvironmentManagedApi.IsManaged(new JsonObject()));
        Assert.Null(EnvironmentManagedApi.IsManaged(null));
    }

    // ---- enable ----

    [Fact]
    public async Task Enable_whatif_shows_the_current_state_and_sends_no_post()
    {
        var service = new Service();
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(disable: false, whatIf: true));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(0, Posts(handler));
        var text = _out.ToString();
        Assert.Contains("environment 'ppac-test' (Sandbox, e1)", text);
        Assert.Contains("Current state: not managed (protectionLevel Basic).", text);
        Assert.Contains("Request: POST https://", text);
    }

    [Fact]
    public async Task Enable_waits_until_the_environment_shows_as_managed()
    {
        var service = new Service { VisibleAfterPolls = 3 };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(disable: false));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(1, Posts(handler));
        Assert.True(_delays >= 1);
        Assert.Contains("Managed Environments is now on", _out.ToString());
        Assert.Contains("/enableManaged?", handler.Requests.Single(r => r.Method == HttpMethod.Post).RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Enable_json_reports_status_and_state()
    {
        var service = new Service();

        var exitCode = await Run(service.Handler, Change(disable: false, json: true));

        Assert.Equal(ExitCodes.Success, exitCode);
        var result = JsonNode.Parse(_out.ToString())!;
        Assert.True((bool)result["accepted"]!);
        Assert.Equal(202, (int)result["status"]!);
        Assert.Equal("confirmed", result["state"]!.ToString());
    }

    [Fact]
    public async Task Enable_when_already_managed_sends_nothing_and_succeeds()
    {
        var service = new Service { Level = "Standard" };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(disable: false));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(0, Posts(handler));
        Assert.Contains("already managed", _out.ToString());
    }

    [Fact]
    public async Task Enable_no_wait_returns_after_the_post_without_checking_again()
    {
        var service = new Service { VisibleAfterPolls = -1 };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(disable: false, noWait: true));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(0, _delays);
        Assert.Contains("not checked", _out.ToString());
    }

    [Fact]
    public async Task Enable_times_out_with_exit_1_and_a_json_error_when_the_change_never_shows_up()
    {
        var service = new Service { VisibleAfterPolls = -1 };

        var exitCode = await Run(service.Handler, Change(disable: false, timeout: 6, json: true));

        Assert.Equal(ExitCodes.Failure, exitCode);
        var error = JsonNode.Parse(_out.ToString())!["error"]!;
        Assert.Equal("timeout", error["details"]!["state"]!.ToString());
        Assert.True((bool)error["details"]!["accepted"]!);
        Assert.Contains("not visible after 6 seconds", error["detail"]!.ToString());
    }

    [Fact]
    public async Task Enable_reports_unconfirmed_when_the_environment_has_no_protection_level()
    {
        var service = new Service { Level = null, VisibleAfterPolls = -1 };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(disable: false));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(1, Posts(handler));
        Assert.Contains("unconfirmed", _out.ToString());
    }

    [Fact]
    public async Task Enable_a_400_validation_response_becomes_a_readable_api_exception()
    {
        var service = new Service
        {
            PostStatus = HttpStatusCode.BadRequest,
            PostBody = "{\"errorDetail\":{\"code\":\"ValidationFailed\",\"fieldErrors\":{\"license\":{\"errorMessages\":[\"No premium license.\"],\"suggestedValue\":\"assign one\"}}}}"
        };

        var ex = await Assert.ThrowsAsync<ApiException>(() => Run(service.Handler, Change(disable: false)));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("ValidationFailed; license: No premium license. (suggested value: assign one)", ex.Message);
    }

    [Fact]
    public async Task An_unknown_environment_is_a_404_api_exception_and_sends_no_post()
    {
        var service = new Service { GetStatus = HttpStatusCode.NotFound };
        var handler = service.Handler;

        var ex = await Assert.ThrowsAsync<ApiException>(() => Run(handler, Change(disable: false)));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal(0, Posts(handler));
    }

    // ---- disable ----

    [Fact]
    public async Task Disable_prompt_declined_exits_6_and_sends_nothing()
    {
        var service = new Service { Level = "Standard" };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(disable: true, interactive: true), answer: "n");

        Assert.Equal(ExitCodes.Declined, exitCode);
        Assert.Equal(0, Posts(handler));
        Assert.Contains("Continue? [y/N]", _err.ToString());
        Assert.Contains("ppac-test", _err.ToString());
    }

    [Fact]
    public async Task Disable_with_yes_waits_until_the_environment_is_basic_again()
    {
        var service = new Service { Level = "Standard", VisibleAfterPolls = 2 };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(disable: true, yes: true));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(1, Posts(handler));
        Assert.Contains("/disableManaged?", handler.Requests.Single(r => r.Method == HttpMethod.Post).RequestUri!.AbsoluteUri);
        Assert.Contains("Managed Environments is now off", _out.ToString());
    }

    [Fact]
    public async Task Disable_when_not_managed_sends_nothing_and_succeeds()
    {
        var service = new Service { Level = "Basic" };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(disable: true, yes: true));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(0, Posts(handler));
        Assert.Contains("not managed. Nothing to do.", _out.ToString());
    }

    [Fact]
    public async Task Disable_whatif_does_not_prompt_and_sends_nothing()
    {
        var service = new Service { Level = "Standard" };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(disable: true, whatIf: true));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(0, Posts(handler));
        Assert.DoesNotContain("Continue?", _err.ToString());
        Assert.Contains("make sure no Managed Environments capabilities are in use", _out.ToString());
    }

    // ---- inconsistent reads ----

    [Fact]
    public async Task Enable_is_sent_when_the_reads_disagree_even_if_most_say_managed()
    {
        // Live, one environment read Standard 18 times and Basic twice with nothing changing.
        var service = new Service { ReadScript = read => read == 3 ? "Basic" : "Standard" };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(disable: false));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(1, Posts(handler));
        Assert.Contains("disagreed", _err.ToString());
    }

    [Fact]
    public async Task Disable_is_sent_when_one_read_still_says_managed()
    {
        // A stale snapshot can show the old state after a change; "not managed" needs every read to agree.
        var service = new Service { Level = "Basic", ReadScript = read => read == 4 ? "Standard" : "Basic" };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(disable: true, yes: true));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(1, Posts(handler));
    }

    [Fact]
    public async Task Whatif_reports_the_state_as_unclear_when_the_reads_disagree()
    {
        var service = new Service { ReadScript = read => read % 2 == 0 ? "Basic" : "Standard" };

        var exitCode = await Run(service.Handler, Change(disable: false, whatIf: true, json: true));

        Assert.Equal(ExitCodes.Success, exitCode);
        var details = JsonNode.Parse(_out.ToString())!["details"]!;
        Assert.Equal("Mixed", details["stateReads"]!.ToString());
        Assert.Null(details["managed"]);
    }

    [Fact]
    public async Task Enable_with_every_read_agreeing_on_managed_still_does_nothing()
    {
        var service = new Service { Level = "Standard" };
        var handler = service.Handler;

        var exitCode = await Run(handler, Change(disable: false));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(0, Posts(handler));
    }

    // ---- registry ----

    [Fact]
    public void Managed_commands_are_registered_with_the_right_safety_options()
    {
        var enable = CommandRegistry.All.Single(s => s.Name == "environment managed enable");
        var disable = CommandRegistry.All.Single(s => s.Name == "environment managed disable");

        Assert.Equal(CommandClass.Write, enable.Class);
        Assert.Equal(CommandClass.Destructive, disable.Class);
        Assert.All(new[] { enable, disable }, s => Assert.Equal(VerificationStatus.Verified, s.Status));
        Assert.All(new[] { enable, disable }, s => Assert.False(s.McpEligible, s.Name));
        Assert.All(new[] { enable, disable }, s => Assert.False(string.IsNullOrWhiteSpace(s.Api), s.Name));
        Assert.DoesNotContain(enable.Options, o => o.Name == "--yes");
        Assert.Contains(disable.Options, o => o.Name == "--yes");
        Assert.All(new[] { enable, disable }, s => Assert.Contains(s.Options, o => o.Name == "--whatif"));
        Assert.All(new[] { enable, disable }, s => Assert.DoesNotContain(s.Options, o => o.Name == "--validate-only"));
        Assert.All(new[] { enable, disable }, s => Assert.True(s.Options.Single(o => o.Name == "--environment").Required));
    }
}
