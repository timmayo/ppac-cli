using Ppac.Core.Operations;

namespace Ppac.Tests;

public class OperationLocationTests
{
    // The Location header from a live 202 response to DELETE /environmentmanagement/environments/{id}.
    [Fact]
    public void Reads_the_operation_id_from_a_live_Location_header()
    {
        var location = "https://api.powerplatform.com/environmentmanagement/environments/ee9efaf5-2558-ed73-ab0b-e8b22efa7c06/operations/c2236ec3-2ad3-4e7f-bfdd-6473cb2abf18?api-version=1";
        Assert.Equal("c2236ec3-2ad3-4e7f-bfdd-6473cb2abf18", OperationLocation.ParseOperationId(location)!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://api.powerplatform.com/environmentmanagement/environments/abc")]
    [InlineData("https://api.powerplatform.com/environmentmanagement/environments/abc/operations")]
    public void Returns_null_when_there_is_no_operation_id(string location)
    {
        Assert.True(OperationLocation.ParseOperationId(location) is null);
    }

    [Fact]
    public void Null_location_gives_null()
    {
        Assert.True(OperationLocation.ParseOperationId(null) is null);
    }
}