using System.Net;
using System.Text;
using Ppac.Core;
using Ppac.Core.Http;

namespace Ppac.Tests;

public class ApiErrorTests
{
    // Body from a live 400 to POST .../copy?ValidateOnly=true: no code or message, only fieldErrors.
    [Fact]
    public async Task Error_with_only_fieldErrors_shows_the_reason_in_the_message()
    {
        var body = """{"errorDetail":{"fieldErrors":{"InvalidLifecycleOperationRequest":{"errorMessages":["The environment 'ee9efaf5' is not linked to a new CDS 2.0 instance. The following operation is forbidden for unlinked environments: 'CopyFromEnvironment'"]}}}}""";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        var client = new PpApiClient(new HttpClient(handler), new StubTokens(), (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<PpacException>(() => client.SendAsync(HttpMethod.Post, "/x", "{}"));

        Assert.Equal(ExitCodes.General, ex.ExitCode);
        Assert.Contains("InvalidLifecycleOperationRequest: The environment 'ee9efaf5' is not linked to a new CDS 2.0 instance", ex.Message);
    }

    [Fact]
    public async Task Error_with_code_and_message_is_unchanged()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":{"code":"Forbidden","message":"Caller is not an authorized app"}}""", Encoding.UTF8, "application/json"),
        });
        var client = new PpApiClient(new HttpClient(handler), new StubTokens(), (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<PpacException>(() => client.SendAsync(HttpMethod.Get, "/x"));

        Assert.Equal(ExitCodes.Forbidden, ex.ExitCode);
        Assert.Contains("Forbidden: Caller is not an authorized app", ex.Message);
    }
}