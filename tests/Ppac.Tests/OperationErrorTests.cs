using System.Text.Json.Nodes;
using Ppac.Core.Operations;

namespace Ppac.Tests;

public class OperationErrorTests
{
    // Shape from a live failed Create operation: the error sits on the failed stage, not at the top level.
    [Fact]
    public void Failed_stage_error_is_read_from_stageStatuses()
    {
        var operation = (JsonObject)JsonNode.Parse("""
            {"name":"Create","status":"Failed","operationId":"op1","stageStatuses":[
              {"name":"Validate","status":"Succeeded"},
              {"name":"Run","status":"Failed","errorDetail":{"code":"CrmProvisioningFailedForExistingEnvironment",
                "fieldErrors":{"CrmProvisioningFailedForExistingEnvironment":{"errorMessages":["The CDS 2.0 provisioning operation failed due to an internal server error."]}}}},
              {"name":"Finalize","status":"Succeeded"}]}
            """)!;

        Assert.Equal(
            " (CrmProvisioningFailedForExistingEnvironment: The CDS 2.0 provisioning operation failed due to an internal server error.)",
            OperationStatus.FailureDetail(operation));
    }

    [Fact]
    public void Top_level_error_code_is_still_used_when_there_are_no_stages()
    {
        var operation = (JsonObject)JsonNode.Parse("""{"status":"Failed","errorDetail":{"code":"CopyFailed"}}""")!;
        Assert.Equal(" (CopyFailed)", OperationStatus.FailureDetail(operation));
    }

    [Fact]
    public void No_error_information_gives_no_detail()
    {
        var operation = (JsonObject)JsonNode.Parse("""{"status":"Failed"}""")!;
        Assert.Equal("", OperationStatus.FailureDetail(operation));
    }
}