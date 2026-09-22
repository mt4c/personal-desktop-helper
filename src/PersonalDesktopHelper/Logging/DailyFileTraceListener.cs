using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace PersonalDesktopHelper.Logging;

public sealed class DailyFileTraceListener : TraceListener
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Action<Exception> _reportFailure;
    private StreamWriter? _writer;
    private string? _currentPath;
    private bool _writeFailed;
    private bool _disposed;

    public DailyFileTraceListener(string directory, Action<Exception> reportFailure, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(reportFailure);
        DirectoryPath = Path.GetFullPath(directory);
        _reportFailure = reportFailure;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(DirectoryPath);
    }

    public string DirectoryPath { get; }
    public override bool IsThreadSafe => true;

    public override void Write(string? message) => WriteEntry(TraceEventType.Information, message);
    public override void WriteLine(string? message) => WriteEntry(TraceEventType.Information, message);

    public override void TraceEvent(
        TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
    {
        if (Filter?.ShouldTrace(eventCache, source, eventType, id, message, null, null, null) != false)
        {
            WriteEntry(eventType, message);
        }
    }

    public override void TraceEvent(
        TraceEventCache? eventCache, string source, TraceEventType eventType, int id,
        string? format, params object?[]? args)
    {
        TraceEvent(eventCache, source, eventType, id,
            args is null ? format : string.Format(CultureInfo.InvariantCulture, format ?? "", args));
    }

    private void WriteEntry(TraceEventType level, string? message)
    {
        Exception? failure = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var now = _timeProvider.GetUtcNow();
                var path = Path.Combine(DirectoryPath, $"desktop-helper-{now:yyyyMMdd}-{Environment.ProcessId}.log");
                if (_currentPath != path)
                {
                    _writer?.Dispose();
                    _writer = null;
                    _currentPath = null;
                    _writer = new StreamWriter(
                        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                    {
                        AutoFlush = true
                    };
                    _currentPath = path;
                }

                _writer!.WriteLine($"{now:O} [{level}] {message}");
                _writeFailed = false;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (!_writeFailed)
                {
                    failure = error;
                }

                _writeFailed = true;
            }
        }

        if (failure is not null)
        {
            // Report outside the writer lock; the callback must not log through this listener.
            _reportFailure(failure);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _disposed = true;
                _writer?.Dispose();
                _writer = null;
            }
        }

        base.Dispose(disposing);
    }
}
