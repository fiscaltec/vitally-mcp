using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Protected;
using VitallyMcp;
using VitallyMcp.Tools;

namespace VitallyMcp.Tests.Tools;

public class SummaryToolsTests
{
    [Theory]
    [InlineData("vitally.custom.countAllSupportTickets")]
    [InlineData("vitally.custom.countOfOpenCustomerGoals")]
    [InlineData("vitally.custom.npsScore")]
    [InlineData("sfdc.Customer_Health_Score__c")]
    public void DefaultRollupTraits_ContainsKeyRollupPaths(string path)
    {
        SummaryTools.DefaultRollupTraits.Should().Contain(path);
    }

    [Fact]
    public void DefaultObjectNames_AreCustomerGoalsAndProductFeedback()
    {
        SummaryTools.DefaultGoalsObjectName.Should().Be("customerGoals");
        SummaryTools.DefaultProductFeedbackObjectName.Should().Be("productFeedback");
    }

    [Fact]
    public async Task GetOrganizationSummary_ReturnsComposite_UsingDefaults()
    {
        // Routes by URL substring; bodies mirror real shapes (org single object; customObjects
        // {results}; instance search BARE ARRAY).
        var client = BuildRoutedClient();
        var service = TestHelpers.BuildVitallyService(client);

        var json = await SummaryTools.GetOrganizationSummary(service, "org-1");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("organization").GetProperty("name").GetString().Should().Be("Acme");
        doc.RootElement.GetProperty("goals").GetProperty("results").GetArrayLength().Should().Be(1);
        doc.RootElement.TryGetProperty("productFeedback", out _).Should().BeTrue();
    }

    private static HttpClient BuildRoutedClient()
    {
        var mock = new Mock<HttpMessageHandler>();
        void Route(string contains, string body) =>
            mock.Protected().Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsoluteUri.Contains(contains)),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(() => new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.OK, Content = new StringContent(body) });

        Route("/resources/organizations/org-1", """{ "id":"org-1","name":"Acme","traits":{ "vitally.custom.npsScore": 9 } }""");
        Route("/resources/customObjects?", """{ "results":[ {"id":"co-goals","name":"customerGoals"}, {"id":"co-feedback","name":"productFeedback"} ] }""");
        Route("/resources/customObjects/co-goals/instances/search", """[ {"id":"g-1","name":"Goal","organizationId":"org-1"} ]""");
        Route("/resources/customObjects/co-feedback/instances/search", "[]");
        return new HttpClient(mock.Object);
    }
}
