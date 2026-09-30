using System.Diagnostics;
using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Notifications;

/// <summary>
/// A slow or misbehaving non-primary transport must not be able to hold the single-reader notification pump
/// (gh#1157, ADR-0019). The fan-out awaits every lane, so the bound sits on the Discord lane: the pump waits for
/// it at most its deadline, that lane is then "not accepted" for ITS OWN dedup memory (so the next re-emission
/// retries it), and the Pushover lane is never gated on it and never bounded by it.
/// </summary>
public class BoundedNotificationChannelTests
{
    private static readonly TimeSpan _deadline = TimeSpan.FromMilliseconds(200);

    private static Notification Note() =>
        new(NotificationSeverity.Notify, "Reviewed setup available", "ES long setup.", "suggestion:ES:1");

    private static DedupingNotificationChannel Dedup(INotificationChannel inner) =>
        new(inner, NullLogger<DedupingNotificationChannel>.Instance);

    private static INotificationChannel Pushover(bool sends = true)
    {
        INotificationChannel fake = A.Fake<INotificationChannel>();
        A.CallTo(() => fake.SendAsync(A<Notification>._, A<CancellationToken>._)).Returns(sends);
        A.CallTo(() => fake.ResolveAsync(A<string>._, A<CancellationToken>._)).Returns(true);
        return fake;
    }

    /// <summary>A Discord that never answers and ignores its token: the worst case for the pump.</summary>
    private static INotificationChannel HungDiscord(out Func<CancellationToken?> tokenSeen)
    {
        CancellationToken? seen = null;
        INotificationChannel fake = A.Fake<INotificationChannel>();
        A.CallTo(() => fake.SendAsync(A<Notification>._, A<CancellationToken>._))
            .Invokes(call => seen = call.GetArgument<CancellationToken>(1))
            .ReturnsLazily(() => new TaskCompletionSource<bool>().Task);
        tokenSeen = () => seen;
        return fake;
    }

    private static FanOutNotificationChannel FanOut(
        INotificationChannel pushover, INotificationChannel discord, CapturingLogger logger) =>
        new([Dedup(pushover), Dedup(new BoundedNotificationChannel(discord, "Discord", _deadline, logger))]);

    // --- (a) a Discord that never completes ---

    [Fact]
    public async Task SendAsync_ShouldReturnNearTheDeadlineWithPushoverSentOnce_WhenDiscordNeverCompletes()
    {
        INotificationChannel pushover = Pushover();
        INotificationChannel discord = HungDiscord(out Func<CancellationToken?> tokenSeen);
        CapturingLogger logger = new();
        FanOutNotificationChannel fanOut = FanOut(pushover, discord, logger);
        Notification note = Note();
        Stopwatch clock = Stopwatch.StartNew();

        bool sent = await fanOut.SendAsync(note, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        clock.Stop();
        sent.Should().BeTrue("Pushover accepted it");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(1500), "the pump waits on Discord at most its deadline");
        A.CallTo(() => pushover.SendAsync(note, CancellationToken.None)).MustHaveHappenedOnceExactly();
        A.CallTo(() => discord.SendAsync(note, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        tokenSeen()!.Value.IsCancellationRequested.Should().BeTrue("the abandoned Discord call is told to stop");
    }

    [Fact]
    public async Task SendAsync_ShouldRetryOnlyDiscordOnTheNextEmission_WhenDiscordTimedOut()
    {
        INotificationChannel pushover = Pushover();
        INotificationChannel discord = HungDiscord(out _);
        FanOutNotificationChannel fanOut = FanOut(pushover, discord, new CapturingLogger());

        await fanOut.SendAsync(Note(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        bool repeat = await fanOut.SendAsync(Note(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        repeat.Should().BeFalse("Pushover already reported it and Discord again did not accept");
        A.CallTo(() => pushover.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => discord.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task SendAsync_ShouldReportNotAccepted_WhenPushoverFailsAndDiscordNeverCompletes()
    {
        FanOutNotificationChannel fanOut = FanOut(Pushover(sends: false), HungDiscord(out _), new CapturingLogger());

        bool sent = await fanOut.SendAsync(Note(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        sent.Should().BeFalse("no transport delivered, so dedup treats it as not told");
    }

    [Fact]
    public async Task SendAsync_ShouldLogOneTimeoutWithNoExceptionOrUrl_WhenDiscordNeverCompletes()
    {
        CapturingLogger logger = new();
        FanOutNotificationChannel fanOut = FanOut(Pushover(), HungDiscord(out _), logger);

        await fanOut.SendAsync(Note(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Warning);
        logger.Entries[0].Message.Should().Contain("Discord").And.Contain("deadline");
        logger.Entries[0].ExceptionText.Should().BeEmpty("no exception object is logged");
    }

    // --- (b) a lane that throws something that is not an HTTP error ---

    [Fact]
    public async Task SendAsync_ShouldStillSendPushoverAndNotEscape_WhenDiscordThrowsANonHttpException()
    {
        INotificationChannel pushover = Pushover();
        INotificationChannel discord = A.Fake<INotificationChannel>();
        A.CallTo(() => discord.SendAsync(A<Notification>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("https://discord.com/api/webhooks/1/leaky-token"));
        CapturingLogger logger = new();
        FanOutNotificationChannel fanOut = FanOut(pushover, discord, logger);

        bool sent = await fanOut.SendAsync(Note(), CancellationToken.None);
        bool repeat = await fanOut.SendAsync(Note(), CancellationToken.None);

        sent.Should().BeTrue();
        repeat.Should().BeFalse();
        A.CallTo(() => pushover.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => discord.SendAsync(A<Notification>._, A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly(); // a lane that threw was not recorded as delivered, so it is retried
        string logged = string.Join('\n', logger.Entries.Select(e => e.Message + e.ExceptionText));
        logged.Should().Contain(nameof(InvalidOperationException)).And.NotContain("leaky-token");
    }

    // --- (c) the bound is Discord's alone ---

    [Fact]
    public async Task SendAsync_ShouldWaitForASlowPushoverToItsOwnCompletion_WhateverDiscordsDeadlineIs()
    {
        bool pushoverFinished = false;
        INotificationChannel pushover = A.Fake<INotificationChannel>();
        A.CallTo(() => pushover.SendAsync(A<Notification>._, A<CancellationToken>._)).ReturnsLazily(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            pushoverFinished = true;
            return true;
        });
        FanOutNotificationChannel fanOut = FanOut(pushover, Pushover(), new CapturingLogger());

        bool sent = await fanOut.SendAsync(Note(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        sent.Should().BeTrue();
        pushoverFinished.Should().BeTrue("the 200 ms Discord deadline must not shorten the Pushover lane");
    }

    // --- The bound itself ---

    [Fact]
    public async Task SendAsync_ShouldReturnTheInnerResultUntouched_WhenTheTransportAnswersInTime()
    {
        INotificationChannel inner = Pushover(sends: true);
        BoundedNotificationChannel bounded = new(inner, "Discord", _deadline, new CapturingLogger());
        Notification note = Note();

        (await bounded.SendAsync(note, CancellationToken.None)).Should().BeTrue();

        A.CallTo(() => inner.SendAsync(note, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SendAsync_ShouldPropagate_WhenTheCallerCancels()
    {
        INotificationChannel discord = HungDiscord(out _);
        BoundedNotificationChannel bounded = new(discord, "Discord", TimeSpan.FromSeconds(30), new CapturingLogger());
        using CancellationTokenSource cancelled = new();
        Task<bool> send = bounded.SendAsync(Note(), cancelled.Token);
        await cancelled.CancelAsync();

        Func<Task> act = () => send.WaitAsync(TimeSpan.FromSeconds(3));

        await act.Should().ThrowAsync<OperationCanceledException>("a real shutdown is not a Discord timeout");
    }

    [Fact]
    public async Task ResolveAsync_ShouldForwardToTheTransport()
    {
        INotificationChannel inner = Pushover();
        BoundedNotificationChannel bounded = new(inner, "Discord", _deadline, new CapturingLogger());

        (await bounded.ResolveAsync("k", CancellationToken.None)).Should().BeTrue();

        A.CallTo(() => inner.ResolveAsync("k", CancellationToken.None)).MustHaveHappenedOnceExactly();
    }

    private sealed class CapturingLogger : ILogger<BoundedNotificationChannel>
    {
        public List<(LogLevel Level, string Message, string ExceptionText)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(
            LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lock (Entries)
            {
                Entries.Add((level, formatter(state, error), error?.ToString() ?? string.Empty));
            }
        }
    }
}
