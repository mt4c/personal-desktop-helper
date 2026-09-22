using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PersonalDesktopHelper.Scheduling;

namespace PersonalDesktopHelper.Persistence;

public sealed class JsonStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly object _gate = new();
    private ApplicationState _state;

    public JsonStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
        try
        {
            using var stream = File.OpenRead(FilePath);
            _state = JsonSerializer.Deserialize<ApplicationState>(stream, JsonOptions)
                ?? throw new InvalidDataException("The settings file contains null instead of an application state.");
            _state.Validate();
        }
        catch (FileNotFoundException)
        {
            IsNew = true;
            _state = ApplicationState.Default;
        }
        catch (DirectoryNotFoundException)
        {
            IsNew = true;
            _state = ApplicationState.Default;
        }
        catch (Exception error) when (error is JsonException or ArgumentException
            or TimeZoneNotFoundException or InvalidTimeZoneException or NotSupportedException)
        {
            throw new InvalidDataException($"The settings file is invalid: {FilePath}", error);
        }
    }

    public string FilePath { get; }
    public bool IsNew { get; }

    public ApplicationState State
    {
        get
        {
            lock (_gate)
            {
                return _state with { Tasks = _state.Tasks.ToArray() };
            }
        }
    }

    public void SetNotificationsEnabled(bool enabled)
    {
        lock (_gate)
        {
            Save(_state with { NotificationsEnabled = enabled });
        }
    }

    public void SetTasks(IReadOnlyList<ScheduledTaskState> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        lock (_gate)
        {
            Save(_state with { Tasks = tasks.ToArray() });
        }
    }

    private void Save(ApplicationState state)
    {
        state.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporaryPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
            _state = state;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
