using Microsoft.Extensions.Logging;
using Moist.Core.Diagnostics;

namespace Moist.Core.Tests;

/// <summary>The observability plumbing: the session-file logger and the last-resort crash log.</summary>
public class DiagnosticsTests
{
    [Fact]
    public void FileLoggerProvider_WritesFormattedLines_AndRespectsMinimum()
    {
        var path = Path.Combine(Path.GetTempPath(), $"moist_logtest_{Guid.NewGuid():N}.log");
        try
        {
            using var provider = new FileLoggerProvider(path, LogLevel.Information);
            var logger = provider.CreateLogger("Test.Category");
            logger.LogDebug("hidden");                                  // below the minimum — must not be written
            logger.LogInformation("hello {Who}", "world");
            logger.LogError(new InvalidOperationException("boom"), "failed");

            var text = File.ReadAllText(path);
            Assert.DoesNotContain("hidden", text);
            Assert.Contains("info: Test.Category hello world", text);
            Assert.Contains("fail: Test.Category failed", text);
            Assert.Contains("InvalidOperationException: boom", text);  // the exception rides along
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void CrashLog_Write_ProducesFileNamingHostSourceAndException()
    {
        var path = CrashLog.Write("test", "unit", new InvalidOperationException("kaboom"));
        Assert.NotNull(path);
        try
        {
            var text = File.ReadAllText(path!);
            Assert.Contains("moist test — unit", text);
            Assert.Contains("kaboom", text);
        }
        finally { try { File.Delete(path!); } catch { } }
    }
}
