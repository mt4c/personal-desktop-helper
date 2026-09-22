using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace PersonalDesktopHelper.Tests;

internal sealed class ObservedTimeProvider : TimeProvider
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 22, 12, 0, 30, TimeSpan.Zero));
    private readonly Channel<TimeSpan> _delays = Channel.CreateUnbounded<TimeSpan>();

    public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public override long GetTimestamp() => _clock.GetTimestamp();
    public override long TimestampFrequency => _clock.TimestampFrequency;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = _clock.CreateTimer(callback, state, dueTime, period);
        _delays.Writer.TryWrite(dueTime);
        return timer;
    }

    public void Advance(TimeSpan amount) => _clock.Advance(amount);

    public async Task<TimeSpan> WaitForDelayAsync()
    {
        return await _delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
