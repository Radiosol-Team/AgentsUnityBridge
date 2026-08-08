using AgentsBridge.Local;
using Xunit;

namespace AgentsBridge.Desktop.Tests;

public sealed class UnityHubProjectDiscoveryTests
{
    [Fact]
    public void Parse_ReadsAndOrdersUnityHubV1Projects()
    {
        const string json = """
            {
              "schema_version": "v1",
              "data": {
                "C:\\Projects\\Older": {
                  "title": "Older",
                  "path": "C:\\Projects\\Older",
                  "version": "2022.3.7f1",
                  "lastModified": 1000
                },
                "C:\\Projects\\Recent": {
                  "title": "Recent",
                  "path": "C:\\Projects\\Recent",
                  "version": "6000.3.11f1",
                  "lastModified": 2000
                }
              }
            }
            """;

        IReadOnlyList<UnityProjectInfo> projects = UnityHubProjectDiscovery.Parse(json);

        Assert.Equal(2, projects.Count);
        Assert.Equal("Recent", projects[0].Name);
        Assert.Equal("6000.3.11f1", projects[0].UnityVersion);
        Assert.Equal("Older", projects[1].Name);
    }

    [Fact]
    public void Parse_MissingData_ReturnsEmptyList()
    {
        IReadOnlyList<UnityProjectInfo> projects = UnityHubProjectDiscovery.Parse("{\"schema_version\":\"v1\"}");

        Assert.Empty(projects);
    }
}
