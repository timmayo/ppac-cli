using System.Net;
using System.Text.Json.Nodes;
using Ppac.Cli.Api;
using Ppac.Cli.Output;
using Ppac.Cli.Registry;
using Ppac.Cli.Tests.TestSupport;

namespace Ppac.Cli.Tests;

public class ExitCodeMappingTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 3)]
    [InlineData(HttpStatusCode.Forbidden, 3)]
    [InlineData(HttpStatusCode.NotFound, 4)]
    [InlineData(HttpStatusCode.TooManyRequests, 5)]
    [InlineData(HttpStatusCode.RequestTimeout, 5)]
    [InlineData(HttpStatusCode.InternalServerError, 5)]
    [InlineData(HttpStatusCode.BadGateway, 5)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 5)]
    [InlineData(HttpStatusCode.BadRequest, 1)]
    [InlineData(HttpStatusCode.Conflict, 1)]
    public void Http_status_maps_to_the_documented_exit_code(HttpStatusCode status, int expected) =>
        Assert.Equal(expected, ExitCodes.ForStatus(status));

    [Fact]
    public void Exit_code_values_are_stable()
    {
        Assert.Equal(0, ExitCodes.Success);
        Assert.Equal(1, ExitCodes.Failure);
        Assert.Equal(2, ExitCodes.Usage);
        Assert.Equal(3, ExitCodes.AuthRequired);
        Assert.Equal(4, ExitCodes.NotFound);
        Assert.Equal(5, ExitCodes.Retryable);
        Assert.Equal(6, ExitCodes.Declined);
    }

    [Fact]
    public void Every_exit_code_has_a_description()
    {
        foreach (var code in ExitCodes.All)
        {
            Assert.NotEqual("unknown", ExitCodes.Describe(code));
        }
    }

    [Fact]
    public async Task Problem_details_detail_is_used_as_the_error_message()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.BadRequest,
            "{\"type\":\"t\",\"title\":\"Bad Request\",\"status\":400,\"detail\":\"Label is too long.\"}"));
        var api = new PowerPlatformApi(new HttpClient(handler), "t");

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => api.GetJsonAsync("https://example.test/x", CancellationToken.None));

        Assert.Equal("Label is too long.", ex.Message);
    }

    [Theory]
    [InlineData("\"A group with this name already exists.\"", "A group with this name already exists.")]
    [InlineData("{\"error\":\"Name taken.\"}", "Name taken.")]
    [InlineData("{\"message\":\"Name taken.\"}", "Name taken.")]
    [InlineData("{\"error\":{\"message\":\"Not allowed.\"}}", "Not allowed.")]
    [InlineData("{\"errorDetail\":{\"code\":\"Conflict\"}}", "Conflict")]
    [InlineData("{\"errorDetail\":{\"fieldErrors\":{\"a\":{\"errorMessages\":[\"x\",\"y\"]},\"b\":{}}}}", "a: x y; b: invalid")]
    [InlineData("{\"errorDetail\":{}}", "{\"errorDetail\":{}}")]
    [InlineData("[\"a\",\"b\"]", "[\"a\",\"b\"]")]
    [InlineData("123", "123")]
    [InlineData("null", "null")]
    [InlineData("not json at all", "not json at all")]
    public async Task Error_bodies_of_any_json_shape_never_crash_and_give_a_message(string body, string expected)
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.BadRequest, body));
        var api = new PowerPlatformApi(new HttpClient(handler), "t");

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => api.SendAsync(new PlannedRequest(HttpMethod.Post, "https://example.test/x"), CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal(expected, ex.Message);
    }

    [Fact]
    public async Task Problem_details_title_is_used_when_there_is_no_detail()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.BadRequest, "{\"title\":\"Bad Request\",\"status\":400}"));
        var api = new PowerPlatformApi(new HttpClient(handler), "t");

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => api.GetJsonAsync("https://example.test/x", CancellationToken.None));

        Assert.Equal("Bad Request", ex.Message);
    }
}

[Collection("GlobalState")]
public sealed class ErrorReportTests : IDisposable
{
    private readonly TextWriter _previousOut = Console.Out;
    private readonly TextWriter _previousErr = Console.Error;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public ErrorReportTests()
    {
        Console.SetOut(_out);
        Console.SetError(_err);
    }

    public void Dispose()
    {
        Console.SetOut(_previousOut);
        Console.SetError(_previousErr);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "Not Found")]
    [InlineData(HttpStatusCode.Forbidden, "Forbidden")]
    [InlineData(HttpStatusCode.TooManyRequests, "Too Many Requests")]
    [InlineData(HttpStatusCode.InternalServerError, "Internal Server Error")]
    public void Status_title_has_spaces_between_words(HttpStatusCode status, string expected) =>
        Assert.Equal(expected, ErrorReport.StatusTitle(status));

    [Fact]
    public void Json_detail_uses_the_service_message_when_given()
    {
        ErrorReport.Write(ExitCodes.NotFound, "The request failed: 404 Not Found. Gone.", json: true,
            status: 404, title: "Not Found", detail: "Gone.");

        var error = JsonNode.Parse(_out.ToString())!["error"]!;
        Assert.Equal("Gone.", error["detail"]!.ToString());
        Assert.Contains("The request failed", _err.ToString());
    }

    [Fact]
    public void Text_mode_writes_only_to_stderr_and_returns_the_code()
    {
        var code = ErrorReport.Write(ExitCodes.AuthRequired, "Denied.", json: false, hint: "Add the permission.");

        Assert.Equal(3, code);
        Assert.Equal("", _out.ToString());
        Assert.Contains("Denied.", _err.ToString());
        Assert.Contains("Add the permission.", _err.ToString());
    }

    [Fact]
    public void Json_mode_writes_an_error_object_to_stdout_and_text_to_stderr()
    {
        var code = ErrorReport.Write(
            ExitCodes.AuthRequired, "Denied.", json: true, status: 403, title: "Forbidden", hint: "Add the permission.");

        Assert.Equal(3, code);
        var error = JsonNode.Parse(_out.ToString())!["error"]!;
        Assert.Equal(3, (int)error["exitCode"]!);
        Assert.Equal(403, (int)error["status"]!);
        Assert.Equal("Forbidden", error["title"]!.ToString());
        Assert.Equal("Denied.", error["detail"]!.ToString());
        Assert.Equal("Add the permission.", error["hint"]!.ToString());
        Assert.Contains("Denied.", _err.ToString());
    }

    [Fact]
    public void Json_mode_omits_fields_that_were_not_given()
    {
        ErrorReport.Write(ExitCodes.Retryable, "Offline.", json: true);

        var error = JsonNode.Parse(_out.ToString())!["error"]!.AsObject();
        Assert.False(error.ContainsKey("status"));
        Assert.False(error.ContainsKey("hint"));
        Assert.False(error.ContainsKey("details"));
    }
}
