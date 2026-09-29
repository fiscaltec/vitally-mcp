using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace VitallyMcp.Tests;

/// <summary>
/// The capture helper is itself load-bearing: several assertions rest on <c>Exceptions[i]</c>
/// belonging to <c>Entries[i]</c>, and <c>Get_organization_summary</c> logs from two sections running
/// concurrently. Unsynchronised <c>List.Add</c> calls can drop entries or misalign the two lists, and
/// a test reading a misaligned pair would assert about the wrong record (Copilot on #177).
/// </summary>
public class CapturingLoggerTests
{
    [Fact]
    public void ConcurrentWrites_LoseNothing_AndKeepEachExceptionWithItsEntry()
    {
        const int writes = 20_000;
        var logger = new CapturingLogger<object>();

        Parallel.For(0, writes, i =>
        {
            var marker = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            logger.Log(LogLevel.Warning, default, marker, new InvalidOperationException(marker), (s, _) => s);
        });

        logger.Entries.Should().HaveCount(writes);
        logger.Exceptions.Should().HaveCount(writes);
        for (var i = 0; i < writes; i++)
        {
            logger.Exceptions[i]!.Message.Should().Be(logger.Entries[i].Message,
                "an entry and its exception are recorded as one unit");
        }
    }
}
