using System.Diagnostics;
using System.Collections.Frozen;

namespace PersonalDesktopHelper.Scheduling;

public sealed class Scheduler : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Action<string, Exception> _reportFailure;
    private readonly Action<IReadOnlyList<ScheduledTaskState>>? _persistTasks;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, Func<IReadOnlyDictionary<string, string>, CancellationToken, Task>> _handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Registration> _tasks = [];
    private readonly List<Registration> _runners = [];
    private bool _stopping;
    private Task? _stopTask;

    public Scheduler(
        Action<string, Exception> reportFailure,
        TimeProvider? timeProvider = null,
        Action<IReadOnlyList<ScheduledTaskState>>? persistTasks = null)
    {
        ArgumentNullException.ThrowIfNull(reportFailure);
        _reportFailure = reportFailure;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _persistTasks = persistTasks;
    }

    public event EventHandler? TasksChanged;

    public void RegisterHandler(string handlerId, Func<CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        RegisterHandler(handlerId, (_, token) => action(token));
    }

    public void RegisterHandler(string handlerId, Func<IReadOnlyDictionary<string, string>, CancellationToken, Task> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerId);
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            _handlers.Add(handlerId, action);
            foreach (var task in _tasks.Values.Where(task => task.State.HandlerId == handlerId))
            {
                Pulse(task);
            }
        }

        OnTasksChanged();
    }

    public IReadOnlyList<string> GetHandlerIds()
    {
        lock (_gate)
        {
            return _handlers.Keys.Order(StringComparer.Ordinal).ToArray();
        }
    }

    public Guid AddTask(string name, string handlerId, TaskSchedule schedule, IReadOnlyDictionary<string, string>? parameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerId);
        ArgumentNullException.ThrowIfNull(schedule);
        Guid id;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (!_handlers.ContainsKey(handlerId))
            {
                throw new ArgumentException("Register the task handler before adding a task.", nameof(handlerId));
            }

            var next = schedule.GetNextOccurrence(_timeProvider.GetUtcNow())
                ?? throw new ArgumentException("The schedule has no future occurrence.", nameof(schedule));
            var state = new ScheduledTaskState
            {
                Id = Guid.NewGuid(), Name = name, HandlerId = handlerId, Schedule = schedule,
                IsEnabled = true, IsCompleted = false, NextRunAt = next,
                Parameters = parameters?.ToFrozenDictionary(StringComparer.Ordinal) ?? FrozenDictionary<string, string>.Empty
            };
            state.Validate();
            _persistTasks?.Invoke([.. _tasks.Values.Select(task => task.State), state]);
            AddRegistration(state);
            id = state.Id;
        }

        OnTasksChanged();
        Trace.TraceInformation("Scheduled task added: '{0}' ({1}), handler '{2}'.", name, id, handlerId);
        return id;
    }

    public IReadOnlyList<ScheduledTaskState> RestoreTasks(IReadOnlyList<ScheduledTaskState> savedTasks)
    {
        ArgumentNullException.ThrowIfNull(savedTasks);
        var skipped = new List<ScheduledTaskState>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_tasks.Count != 0)
            {
                throw new InvalidOperationException("Restore saved tasks before adding new tasks.");
            }

            var states = savedTasks.ToArray();
            if (states.Select(state => state.Id).Distinct().Count() != states.Length)
            {
                throw new ArgumentException("Saved task IDs must be unique.", nameof(savedTasks));
            }

            var now = _timeProvider.GetUtcNow();
            for (var index = 0; index < states.Length; index++)
            {
                var state = states[index];
                state.Validate();
                if (!state.IsCompleted && state.NextRunAt <= now &&
                    (state.IsEnabled || state.Schedule is OneShotSchedule))
                {
                    if (state.IsEnabled)
                    {
                        skipped.Add(state);
                    }

                    states[index] = Advance(state, now);
                }
            }

            if (!states.SequenceEqual(savedTasks))
            {
                _persistTasks?.Invoke(states);
            }

            foreach (var state in states)
            {
                AddRegistration(state);
            }
        }

        OnTasksChanged();
        return skipped;
    }

    public IReadOnlyList<ScheduledTaskInfo> GetTasks()
    {
        lock (_gate)
        {
            return _tasks.Values.Select(task => new ScheduledTaskInfo(
                task.State, task.IsRunning, _handlers.ContainsKey(task.State.HandlerId), task.Error)).ToArray();
        }
    }

    public Task GetCompletion(Guid id)
    {
        lock (_gate)
        {
            return _tasks[id].Completion;
        }
    }

    public void SetEnabled(Guid id, bool enabled)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            var task = _tasks[id];
            if (enabled && task.State.IsCompleted)
            {
                throw new InvalidOperationException("A completed task cannot be enabled again.");
            }

            var state = task.State with { IsEnabled = enabled };
            if (enabled && !task.IsRunning && state.NextRunAt <= _timeProvider.GetUtcNow())
            {
                state = Advance(state, _timeProvider.GetUtcNow());
            }

            SaveChange(task, state);
            task.Error = null;
            Pulse(task);
        }

        OnTasksChanged();
        Trace.TraceInformation("Scheduled task {0} enabled: {1}.", id, enabled);
    }

    public void DeleteTask(Guid id)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            var task = _tasks[id];
            _persistTasks?.Invoke(_tasks.Values.Where(item => item != task).Select(item => item.State).ToArray());
            _tasks.Remove(id);
            task.IsDeleted = true;
            Pulse(task);
        }

        OnTasksChanged();
        Trace.TraceInformation("Scheduled task deleted: {0}.", id);
    }

    private void AddRegistration(ScheduledTaskState state)
    {
        var task = new Registration(state with { Parameters = state.Parameters.ToFrozenDictionary(StringComparer.Ordinal) });
        _tasks.Add(state.Id, task);
        _runners.Add(task);
        task.Completion = Task.Run(() => RunTaskAsync(task, _shutdown.Token));
    }

    private void SaveChange(Registration task, ScheduledTaskState state)
    {
        state.Validate();
        _persistTasks?.Invoke(_tasks.Values.Select(item => item == task ? state : item.State).ToArray());
        task.State = state;
    }

    private static ScheduledTaskState Advance(ScheduledTaskState state, DateTimeOffset now)
    {
        var next = state.Schedule.GetNextAfterRun(state.NextRunAt!.Value, now);
        return state with { NextRunAt = next, IsCompleted = next is null };
    }

    private async Task RunTaskAsync(Registration task, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    Func<IReadOnlyDictionary<string, string>, CancellationToken, Task>? action;
                    DateTimeOffset? next;
                    lock (_gate)
                    {
                        task.Signal.Wait(0);
                        if (task.IsDeleted || task.State.IsCompleted)
                        {
                            return;
                        }

                        _handlers.TryGetValue(task.State.HandlerId, out action);
                        next = task.State.IsEnabled && task.Error is null && action is not null
                            ? task.State.NextRunAt : null;
                    }

                    if (next is null)
                    {
                        await task.Signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var remaining = next.Value - _timeProvider.GetUtcNow();
                    if (remaining > TimeSpan.Zero)
                    {
                        await WaitForChangeAsync(task, remaining, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    lock (_gate)
                    {
                        if (_stopping || task.IsDeleted || !task.State.IsEnabled || task.State.NextRunAt != next)
                        {
                            continue;
                        }

                        // Save consumption before invoking a callback, so completed one-shots never replay on boot.
                        SaveChange(task, Advance(task.State, _timeProvider.GetUtcNow()));
                        task.IsRunning = true;
                    }

                    OnTasksChanged();
                    Trace.TraceInformation("Scheduled task starting: '{0}' ({1}).", task.State.Name, task.State.Id);
                    try
                    {
                        await action!(task.State.Parameters, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        _reportFailure(task.State.Name, error);
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            task.IsRunning = false;
                        }

                        OnTasksChanged();
                        Trace.TraceInformation("Scheduled task run ended: '{0}' ({1}).", task.State.Name, task.State.Id);
                    }

                    lock (_gate)
                    {
                        if (!_stopping && !task.IsDeleted && !task.State.IsCompleted &&
                            task.State.NextRunAt <= _timeProvider.GetUtcNow())
                        {
                            SaveChange(task, Advance(task.State, _timeProvider.GetUtcNow()));
                        }
                    }

                    OnTasksChanged();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    // Persistence or schedule failures pause this registration; they must not execute unsaved work.
                    lock (_gate)
                    {
                        task.Error = error.Message;
                    }

                    _reportFailure(task.State.Name, error);
                    OnTasksChanged();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Quit cancels running callbacks; disabling or deleting a task only wakes its pending wait.
        }
    }

    private async Task WaitForChangeAsync(Registration task, TimeSpan remaining, CancellationToken token)
    {
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(token);
        var delay = Task.Delay(
            remaining < TimeSpan.FromMinutes(1) ? remaining : TimeSpan.FromMinutes(1), _timeProvider, wake.Token);
        var change = task.Signal.WaitAsync(wake.Token);
        var completed = await Task.WhenAny(delay, change).ConfigureAwait(false);
        await wake.CancelAsync().ConfigureAwait(false);
        await completed.ConfigureAwait(false);
    }

    private static void Pulse(Registration task)
    {
        if (task.Signal.CurrentCount == 0)
        {
            task.Signal.Release();
        }
    }

    private void OnTasksChanged() => TasksChanged?.Invoke(this, EventArgs.Empty);

    public void RequestStop()
    {
        lock (_gate)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            _shutdown.Cancel();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            _stopTask ??= StopAsync(_runners.ToArray());
            return new ValueTask(_stopTask);
        }
    }

    private async Task StopAsync(Registration[] tasks)
    {
        try
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(tasks.Select(task => task.Completion)).ConfigureAwait(false);
        }
        finally
        {
            foreach (var task in tasks)
            {
                task.Signal.Dispose();
            }

            _shutdown.Dispose();
        }
    }

    private sealed class Registration(ScheduledTaskState state)
    {
        public ScheduledTaskState State { get; set; } = state;
        public SemaphoreSlim Signal { get; } = new(0, 1);
        public Task Completion { get; set; } = Task.CompletedTask;
        public bool IsRunning { get; set; }
        public bool IsDeleted { get; set; }
        public string? Error { get; set; }
    }
}
