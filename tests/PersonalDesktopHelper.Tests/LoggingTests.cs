using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using PersonalDesktopHelper.Logging;

namespace PersonalDesktopHelper.Tests;

public sealed class LoggingTests
{
    [Fact]
    public void DailyLogsIncludeUtcTimestampSeverityAndRollOver()
    {
        using var directory = new TemporaryDirectory();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 22, 23, 59, 0, TimeSpan.Zero));
        var failures = new List<Exception>();
        using (var log = new DailyFileTraceListener(directory.Path, failures.Add, clock))
        {
            log.TraceEvent(null, "Test", TraceEventType.Information, 0, "First entry");
            clock.Advance(TimeSpan.FromMinutes(2));
            log.TraceEvent(null, "Test", TraceEventType.Error, 0, "Failure: {0}", "details");
        }

        var files = Directory.GetFiles(directory.Path, "*.log").Order().ToArray();
        Assert.Equal(2, files.Length);
        Assert.Contains("20260922", files[0]);
        Assert.Contains("2026-09-22T23:59:00", File.ReadAllText(files[0]));
        Assert.Contains("[Information] First entry", File.ReadAllText(files[0]));
        Assert.Contains("20260923", files[1]);
        Assert.Contains("[Error] Failure: details", File.ReadAllText(files[1]));
        Assert.Empty(failures);
    }

    [Fact]
    public async Task ConcurrentEntriesAreNotInterleavedOrLost()
    {
        using var directory = new TemporaryDirectory();
        var failures = new ConcurrentQueue<Exception>();
        using (var log = new DailyFileTraceListener(directory.Path, failures.Enqueue))
        {
            await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(() =>
                log.TraceEvent(null, "Test", TraceEventType.Information, 0, $"Entry {index}"))));
        }

        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(directory.Path, "*.log")));
        Assert.Equal(100, lines.Length);
        Assert.Equal(100, lines.Distinct().Count());
        Assert.Empty(failures);
    }

    [Fact]
    public void WriteFailuresAreReportedAndLaterWritesCanRecover()
    {
        using var directory = new TemporaryDirectory();
        var logDirectory = Path.Combine(directory.Path, "logs");
        var failures = new List<Exception>();
        using (var log = new DailyFileTraceListener(logDirectory, failures.Add))
        {
            Directory.Delete(logDirectory);
            File.WriteAllText(logDirectory, "Not a directory");
            log.WriteLine("First failure");
            log.WriteLine("Repeated failure");
            Assert.Single(failures);
            File.Delete(logDirectory);
            Directory.CreateDirectory(logDirectory);
            log.WriteLine("Recovered");
        }

        Assert.Contains("Recovered", File.ReadAllText(Assert.Single(Directory.GetFiles(logDirectory, "*.log"))));
    }

    [Fact]
    public async Task CleanupDeletesOnlyMatchingLogsStrictlyOlderThanSevenDays()
    {
        using var directory = new TemporaryDirectory();
        var now = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
        var expired = CreateLog(directory.Path, "desktop-helper-20260914-123.log", now.AddDays(-8));
        var boundary = CreateLog(directory.Path, "desktop-helper-20260915-123.log", now.AddDays(-7));
        var recent = CreateLog(directory.Path, "desktop-helper-20260922-123.log", now);
        var unrelated = CreateLog(directory.Path, "other.log", now.AddDays(-30));
        var malformed = CreateLog(directory.Path, "desktop-helper-not-a-date.log", now.AddDays(-30));
        var child = Directory.CreateDirectory(Path.Combine(directory.Path, "child")).FullName;
        var nested = CreateLog(child, "desktop-helper-20260914-123.log", now.AddDays(-8));

        var result = await LogRetention.CleanupAsync(directory.Path, now);

        Assert.Equal(1, result.DeletedCount);
        Assert.Empty(result.Failures);
        Assert.False(File.Exists(expired));
        Assert.All(new[] { boundary, recent, unrelated, malformed, nested }, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task CleanupReportsLockedFilesAndContinuesWithOtherExpiredLogs()
    {
        using var directory = new TemporaryDirectory();
        var now = DateTimeOffset.UtcNow;
        var lockedPath = CreateLog(directory.Path, "desktop-helper-20260101-123.log", now.AddDays(-8));
        var removable = CreateLog(directory.Path, "desktop-helper-20260101-456.log", now.AddDays(-8));
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await LogRetention.CleanupAsync(directory.Path, now);

        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(lockedPath, Assert.Single(result.Failures).Path);
        Assert.False(File.Exists(removable));
        Assert.True(File.Exists(lockedPath));
    }

    [Fact]
    public async Task CleanupHonorsCancellation()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LogRetention.CleanupAsync(directory.Path, DateTimeOffset.UtcNow, cancellation.Token));
    }

    private static string CreateLog(string directory, string name, DateTimeOffset lastWrite)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "Log content");
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return path;
    }
}
