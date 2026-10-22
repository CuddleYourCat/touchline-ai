using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Touchline.MatchCentre.Api;
using Touchline.MatchCentre.Live;

namespace Touchline.Tests.Unit.MatchCentre;

[TestFixture]
public class HeartbeatStreamTests
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(15);

    [Test]
    public async Task Messages_pass_straight_through_and_the_stream_ends_with_its_source()
    {
        var time = new FakeTimeProvider();
        LiveMessage[] source = [new("event", "1", "a"), new("state", "1", "b")];

        var received = await HeartbeatStream.WithHeartbeats(source.ToAsyncEnumerable(), _interval, time, CancellationToken.None).ToListAsync();

        received.Should().Equal(source);
    }

    [Test]
    public async Task A_heartbeat_goes_out_when_the_source_is_quiet_for_the_interval()
    {
        var time = new FakeTimeProvider();
        var channel = Channel.CreateUnbounded<LiveMessage>();
        await using var stream = HeartbeatStream.WithHeartbeats(Read(channel), _interval, time, CancellationToken.None).GetAsyncEnumerator();

        var next = stream.MoveNextAsync().AsTask();
        time.Advance(_interval - TimeSpan.FromSeconds(1));
        next.IsCompleted.Should().BeFalse("the interval hasn't passed yet");

        time.Advance(TimeSpan.FromSeconds(1));
        (await next).Should().BeTrue();
        stream.Current.Event.Should().Be(StreamEvents.Heartbeat);
        stream.Current.Id.Should().BeNull("a heartbeat isn't a point to resume from");

        await channel.Writer.WriteAsync(new LiveMessage("event", "2", "c"));
        (await stream.MoveNextAsync()).Should().BeTrue();
        stream.Current.Id.Should().Be("2", "the read pending during the heartbeat still delivers the next message");

        channel.Writer.Complete();
        (await stream.MoveNextAsync()).Should().BeFalse();
    }

    [Test]
    public async Task A_busy_stream_sends_no_heartbeat_and_the_quiet_spell_counts_from_the_last_message()
    {
        var time = new FakeTimeProvider();
        var channel = Channel.CreateUnbounded<LiveMessage>();
        await using var stream = HeartbeatStream.WithHeartbeats(Read(channel), _interval, time, CancellationToken.None).GetAsyncEnumerator();

        var next = stream.MoveNextAsync().AsTask();
        time.Advance(TimeSpan.FromSeconds(10));
        await channel.Writer.WriteAsync(new LiveMessage("event", "1", "a"));
        (await next).Should().BeTrue();
        stream.Current.Id.Should().Be("1");

        // Time moves a second at a time, with a pause for the stream's loop to re-arm its timer
        // on another thread; a fake clock that jumps ahead of a timer not yet armed would never fire it.
        var start = time.GetUtcNow() - TimeSpan.FromSeconds(10);
        next = stream.MoveNextAsync().AsTask();
        for (var second = 0; second < 30 && !next.IsCompleted; second++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20);
        }

        (await next).Should().BeTrue();
        stream.Current.Event.Should().Be(StreamEvents.Heartbeat);
        ((Heartbeat)stream.Current.Data).At.Should().Be(start + TimeSpan.FromSeconds(25),
            "the quiet spell starts at the message (10 s), not at the start of the stream");
    }

    [Test]
    public async Task Cancelling_ends_the_stream_cleanly_while_a_read_is_pending()
    {
        var time = new FakeTimeProvider();
        var channel = Channel.CreateUnbounded<LiveMessage>();
        using var disconnect = new CancellationTokenSource();
        var stream = HeartbeatStream.WithHeartbeats(Read(channel), _interval, time, disconnect.Token).GetAsyncEnumerator();

        var next = stream.MoveNextAsync().AsTask();
        await disconnect.CancelAsync();

        await next.Invoking(async n => await n).Should().ThrowAsync<OperationCanceledException>();
        await stream.Invoking(async s => await s.DisposeAsync()).Should().NotThrowAsync();
    }

    private static async IAsyncEnumerable<LiveMessage> Read(Channel<LiveMessage> channel, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return message;
        }
    }
}
