using System.Text.Json.Nodes;
using Ppac.Core.Output;

namespace Ppac.Tests;

public class ColumnsTests
{
    // Shape copied from a real GET /environmentmanagement/environments response (2024-10-01), values anonymized.
    private const string EnvironmentJson = """
        {
          "id": "00000000-0000-0000-0000-000000000001",
          "displayName": "Contoso Dev",
          "tenantId": "00000000-0000-0000-0000-0000000000aa",
          "type": "Sandbox",
          "geo": "unitedstates",
          "azureRegion": "westus",
          "state": "Enabled",
          "url": "https://contosodev.crm.dynamics.com",
          "createdBy": { "id": "00000000-0000-0000-0000-0000000000bb", "type": "User" }
        }
        """;

    [Fact]
    public void Environment_columns_read_real_response_fields()
    {
        var row = (JsonObject)JsonNode.Parse(EnvironmentJson)!;
        var values = Columns.Environments.ToDictionary(c => c.Header, c => OutputWriter.CellText(row, c.Path));

        Assert.Equal("Contoso Dev", values["DisplayName"]);
        Assert.Equal("Sandbox", values["Type"]);
        Assert.Equal("Enabled", values["State"]);
        Assert.Equal("westus", values["Region"]);
        Assert.Equal("00000000-0000-0000-0000-000000000001", values["Id"]);
    }

    [Fact]
    public void Environment_table_has_no_blank_columns()
    {
        var row = (JsonObject)JsonNode.Parse(EnvironmentJson)!;
        var writer = new StringWriter { NewLine = "\n" };
        OutputWriter.Write(writer, [row], Columns.Environments, OutputFormat.Table);
        var dataLine = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[2];
        Assert.Contains("Contoso Dev", dataLine);
        Assert.Contains("Sandbox", dataLine);
        Assert.Contains("westus", dataLine);
    }
}