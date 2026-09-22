using System.IO;
using System.Text.RegularExpressions;

namespace PersonalDesktopHelper.Logging;

public sealed record LogCleanupFailure(string Path, Exception Error);
public sealed record LogCleanupResult(int DeletedCount, IReadOnlyList<LogCleanupFailure> Failures);

public static partial class LogRetention
{
    [GeneratedRegex(@"^desktop-helper-\d{8}-\d+\.log$", RegexOptions.CultureInvariant)]
    private static partial Regex LogFileName();

    public static Task<LogCleanupResult> CleanupAsync(
        string directory, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Task.Run(() =>
        {
            var cutoff = now.UtcDateTime.AddDays(-7);
            var deleted = 0;
            var failures = new List<LogCleanupFailure>();
            foreach (var path in Directory.EnumerateFiles(directory, "desktop-helper-*.log", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!LogFileName().IsMatch(Path.GetFileName(path)))
                {
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
                        File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                        deleted++;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    failures.Add(new LogCleanupFailure(path, error));
                }
            }

            return new LogCleanupResult(deleted, failures);
        }, cancellationToken);
    }
}
