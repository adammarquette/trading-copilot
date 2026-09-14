using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Chat;
using MarqSpec.TradingCopilot.Api.Orders;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Chat;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api.Chat;

/// <summary>
/// Independent QA coverage for <see cref="IChatTurnGuard"/>'s <b>real</b> contention path (gh#1118, of gh#1106,
/// shipped in PR #1116; R-6) — the Postgres advisory lock that makes "one in-flight turn per conversation" true.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this tier, and why nothing already covers it.</b> The unit tier cannot cover this by construction: the
/// lock is raw SQL the EF in-memory provider does not support, so <c>ChatTurnEndpointTests</c> drives an in-process
/// <c>SemaphoreSlim</c> double (<c>SerializingChatTurnGuard</c>) instead — which proves the <b>endpoint</b> uses the
/// seam correctly under a genuine interleave, but nothing about <c>ChatTurnGuard</c> itself. The four existing
/// <c>/turns</c> Testcontainers suites (<c>ChatToolLayerIntegrationTests</c> and the grounding suites) run every
/// call through the real guard, which proves the SQL <b>executes</b>, but none of them drives two concurrent turns,
/// so the guarantee itself was untested. This matters more than an ordinary seam because R-6 and the data
/// dictionary's chat page assert the guarantee as landed, and <c>RealtimeChatChunk</c>'s doc comment was changed
/// from "an assumption" to "a guarantee the server keeps" — those claims rested on code review alone before this
/// suite.
/// </para>
/// <para>
/// <b>The shape, reused deliberately.</b> <see cref="IChatTurnGuard"/> is modelled on
/// <see cref="IAccountEntryGuard"/> (gh#531/gh#589), whose own concurrency coverage is
/// <c>AccountEntryConcurrencyIntegrationTests</c> — this suite follows that one's technique: force the interleave
/// from <b>inside</b> the guarded work (here, the scripted model call,
/// <see cref="ScriptedChatLlmProvider.OnCall"/>), never sample a wall clock, and prove promptness after an abort by
/// pinning a second, genuinely separate connection <b>before</b> the aborting call so Npgsql cannot hand it back
/// the very connector whose pool reset would mask a missing cleanup.
/// </para>
/// <para>
/// <b>One structural difference from the account-entry sibling.</b> <c>IAccountEntryGuard.RunExclusiveAsync</c> is
/// <b>blocking</b> (<c>pg_advisory_lock</c>), so a peer racing it must be sampled, never awaited inline — awaiting
/// would deadlock the test on the very lock under test. <c>IChatTurnGuard.TryRunExclusiveAsync</c> is
/// <b>non-blocking</b> (<c>pg_try_advisory_lock</c>) for every caller, including a peer racing the SAME
/// conversation: it fails fast rather than parking, so the single-conversation race below safely <b>awaits</b> the
/// peer inline instead of sampling — there is no deadlock risk to guard against.
/// </para>
/// </remarks>
public class ChatTurnGuardContentionIntegrationTests : IClassFixture<ChatTurnGuardTestPostgresFactory>
{
    private readonly ChatTurnGuardTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private sealed record LoginTokenResponse(string Token);

    /// <summary>The 409 refusal envelope's shape (<c>ApiResult.layer</c>) — read independently of the endpoint.</summary>
    private sealed record RefusalEnvelope(string Error, string? Layer);

    public ChatTurnGuardContentionIntegrationTests(ChatTurnGuardTestPostgresFactory factory)
    {
        _factory = factory;
    }

    // =================================================================================================================
    // Case 1 (gh#1118): two genuinely concurrent turns on ONE conversation.
    // =================================================================================================================

    [Fact]
    public async Task TwoTurnsOnOneConversation_ShouldRunExactlyOne_AndRefuseThePeerContributingNothing()
    {
        _factory.Llm.Reset();
        HttpClient client = await AuthenticatedClientAsync();
        Guid conversationId = await StartConversationAsync(client);

        HttpResponseMessage? peerResponse = null;
        bool interleaveRan = false;

        // Launched and AWAITED from INSIDE the first turn's model call, while ChatTurnGuard's advisory lock is
        // still held — the SQL unlock in TryRunExclusiveAsync's finally runs only once this whole callback
        // (RunTurnAsync) returns, and the model call is nested deep inside it. Awaiting the peer inline is safe
        // here specifically because ITS OWN guard check is non-blocking (see the class remarks): a busy
        // conversation refuses immediately rather than parking, so there is no deadlock risk.
        _factory.Llm.OnCall(async () =>
        {
            interleaveRan = true;
            peerResponse = await TakeTurnAsync(client, conversationId, "peer message, must never persist");
        });
        _factory.Llm.Script(_ => ScriptedChatLlmProvider.Answer("first reply"));

        using HttpResponseMessage first = await TakeTurnAsync(client, conversationId, "first message");
        _factory.Llm.OnCall(null); // unconditional: this callback is fixture-lifetime and must not leak into the next test

        interleaveRan.Should().BeTrue("the interleave must have run — otherwise nothing was raced");
        using HttpResponseMessage peer = peerResponse
            ?? throw new InvalidOperationException("gh#1118: the peer request never completed.");

        first.StatusCode.Should().Be(
            HttpStatusCode.OK, "the first turn — which actually held the advisory lock — must complete normally");
        peer.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            "a second turn racing an in-flight one on the SAME conversation must be refused: it reached the guard "
            + "while the first genuinely still held the lock, mid model-call");
        RefusalEnvelope refusal = await ReadAsync<RefusalEnvelope>(peer);
        refusal.Layer.Should().Be(
            "chat-turn-in-flight",
            "the client must be able to tell this refusal apart from the endpoint's OTHER 409 (the lost sequence "
            + "race on the assistant append)");

        ConversationDetailResponse thread = await ReadConversationAsync(client, conversationId);
        thread.Messages.Should().HaveCount(
            2,
            "exactly ONE turn pair — the refused peer must contribute NOTHING at all, not even the operator "
            + "message it would have persisted first (the guard wraps the persist too)");
        thread.Messages[0].Role.Should().Be(ChatRole.User);
        thread.Messages[0].Content.Should().Be(
            "first message", "the winning turn's own content — the peer's content must never have reached the thread");
        thread.Messages[1].Role.Should().Be(ChatRole.Assistant);
        thread.Messages[1].Content.Should().Be("first reply");
    }

    // =================================================================================================================
    // Case 2 (gh#1118): two concurrent turns on DIFFERENT conversations must both run.
    // =================================================================================================================

    [Fact]
    public async Task TwoTurnsOnDifferentConversations_ShouldBothRun_BecauseTheLockIsPerConversationNotPerEndpoint()
    {
        _factory.Llm.Reset();
        HttpClient client = await AuthenticatedClientAsync();
        Guid conversationA = await StartConversationAsync(client);
        Guid conversationB = await StartConversationAsync(client);

        // A BARRIER, not a wall-clock delay: each model call waits until BOTH have arrived before either is let
        // through, so this proves the two turns were genuinely in flight at the same instant rather than merely
        // fast enough not to collide by luck — the guard discipline's "cannot pass by luck" standard.
        TaskCompletionSource bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int arrived = 0;
        _factory.Llm.OnCall(async () =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
            {
                bothArrived.TrySetResult();
            }

            // Time-bounded, deliberately: an un-timed TCS await would hang the whole run — reading as slow CI, not
            // as red — if the guard wrongly serialized these two conversations and only one call ever arrived.
            await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        });
        _factory.Llm.ScriptAlways(_ => ScriptedChatLlmProvider.Answer("reply"));

        Task<HttpResponseMessage> turnA = TakeTurnAsync(client, conversationA, "message A");
        Task<HttpResponseMessage> turnB = TakeTurnAsync(client, conversationB, "message B");
        await Task.WhenAll(turnA, turnB);
        _factory.Llm.OnCall(null);

        using HttpResponseMessage responseA = await turnA;
        using HttpResponseMessage responseB = await turnB;

        responseA.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the lock must serialize a CONVERSATION, never the endpoint — two DIFFERENT conversations must both "
            + "run even though their model calls were provably in flight at the same instant");
        responseB.StatusCode.Should().Be(HttpStatusCode.OK, "same reasoning as conversation A, the other direction");

        ConversationDetailResponse threadA = await ReadConversationAsync(client, conversationA);
        ConversationDetailResponse threadB = await ReadConversationAsync(client, conversationB);
        threadA.Messages.Should().HaveCount(2, "conversation A's turn ran to completion, unrefused");
        threadB.Messages.Should().HaveCount(2, "conversation B's turn ran to completion, unrefused");
    }

    // =================================================================================================================
    // Case 3 (gh#1118): a conversation is immediately lockable again once a turn completes.
    // =================================================================================================================

    [Fact]
    public async Task TryRunExclusiveAsync_ShouldReleaseTheLock_PromptlyOnceATurnCompletes()
    {
        Guid conversationId = Guid.NewGuid();

        // Pinned FIRST, and the ordering is load-bearing (mirrors AccountEntryConcurrencyIntegrationTests' abort
        // case): opening this connection BEFORE the first guard call forces Npgsql to hand that call a DIFFERENT
        // physical connector. Reading the second, "is it free again" call back through the SAME connector the
        // first used would let that connector's OWN explicit unlock make an otherwise-missing cleanup look fine.
        await using AsyncServiceScope watcherScope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext watcherDb = watcherScope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await watcherDb.Database.OpenConnectionAsync();

        try
        {
            await using AsyncServiceScope firstScope = _factory.Services.CreateAsyncScope();
            // ChatTurnGuard is stateless (the context is a parameter, not a captured field) — safe to resolve from
            // one scope and reuse with a DbContext from a wholly different scope, exactly as the account-entry
            // guard's own suite does.
            IChatTurnGuard guard = firstScope.ServiceProvider.GetRequiredService<IChatTurnGuard>();
            TradingCopilotDbContext firstDb = firstScope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();

            int firstResult = await guard.TryRunExclusiveAsync(
                firstDb, conversationId, turn: () => Task.FromResult(1), onBusy: () => -1, CancellationToken.None);
            firstResult.Should().Be(1, "sanity — the first call must actually acquire the lock and run");

            int secondResult = await guard.TryRunExclusiveAsync(
                watcherDb, conversationId, turn: () => Task.FromResult(2), onBusy: () => -1, CancellationToken.None);
            secondResult.Should().Be(
                2,
                "a conversation must be immediately re-lockable once a turn completes — the guard's explicit "
                + "pg_advisory_unlock, not merely Npgsql's DEFERRED pool reset on the first connector's next use, "
                + "is what makes this prompt");
        }
        finally
        {
            await watcherDb.Database.CloseConnectionAsync();
        }
    }

    // =================================================================================================================
    // Case 4 (gh#1118, mirroring gh#1120's landed sibling for IAccountEntryGuard): a conversation is immediately
    // lockable again after a turn FAULTS (a client abort mid-turn).
    // =================================================================================================================

    [Fact]
    public async Task TryRunExclusiveAsync_ShouldReleaseTheLock_AfterAnAbortedTurn_SoTheNextCallIsNotReportedBusy()
    {
        Guid conversationId = Guid.NewGuid();

        // PINNED FIRST, and that ordering is what makes this test able to fail (gh#1120 review, on the sibling
        // case): leasing the watcher's connector only AFTER the abort would let the pool hand the watcher back the
        // very connector the aborted guard just released, whose DISCARD ALL reset would release the leaked session
        // lock before pg_try_advisory_lock ever ran — so the guard would acquire, the busy flag would never trip,
        // and this test would stay green against an unfixed CancellationToken-propagating unlock.
        await using AsyncServiceScope watcherScope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext watcherDb = watcherScope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await watcherDb.Database.OpenConnectionAsync();

        try
        {
            await using AsyncServiceScope abortingScope = _factory.Services.CreateAsyncScope();
            IChatTurnGuard guard = abortingScope.ServiceProvider.GetRequiredService<IChatTurnGuard>();
            TradingCopilotDbContext abortingDb = abortingScope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();

            using CancellationTokenSource requestAborted = new();

            Func<Task<int>> abortMidTurn = async () =>
            {
                // Mirrors the operator paths' shape exactly: the callback spans real work (here, a lock-held yield
                // standing in for the model round-trip) and the CALLER's token — RequestAborted in production —
                // is what goes cancelled, not some unrelated token.
                await Task.Yield();
                requestAborted.Cancel();
                throw new OperationCanceledException(requestAborted.Token);
            };

            await FluentActions.Awaiting(() =>
                    guard.TryRunExclusiveAsync(
                        abortingDb, conversationId, abortMidTurn, onBusy: () => -1, requestAborted.Token))
                .Should().ThrowAsync<OperationCanceledException>(
                    "the abort must propagate to the caller — this guard adds serialization, never swallows a "
                    + "cancellation");

            int fired = -1;
            int result = await guard.TryRunExclusiveAsync(
                watcherDb, conversationId, turn: () => Task.FromResult(1), onBusy: () => fired, CancellationToken.None);

            result.Should().Be(
                1,
                "a conversation must be immediately re-lockable after an aborted turn — reporting busy here would "
                + "leave the NEXT genuine turn on this conversation refused as 'already in flight' for a turn that "
                + "has already ended");
        }
        finally
        {
            await watcherDb.Database.CloseConnectionAsync();
        }
    }

    // =================================================================================================================
    // Case 5 (gh#1118): the two-argument advisory-lock space is disjoint from IAccountEntryGuard's one-argument
    // space.
    // =================================================================================================================

    [Fact]
    public async Task ChatTurnGuard_ShouldNeverShareItsLockSpace_WithAccountEntryGuard_EvenForTheIdenticalId()
    {
        // The SAME Guid used as BOTH the account id AND the conversation id — stronger proof than "an unrelated
        // account": a hashtext collision between two DIFFERENT ids would only demonstrate the guards happened not
        // to collide this run. Using the identical id removes that luck entirely — if the two guards secretly
        // shared one lock space, holding the account-entry lock on this id would make the chat-turn guard's
        // try-lock on the SAME id observe it busy, deterministically, every time.
        Guid sharedId = Guid.NewGuid();

        await using AsyncServiceScope accountScope = _factory.Services.CreateAsyncScope();
        IAccountEntryGuard accountGuard = accountScope.ServiceProvider.GetRequiredService<IAccountEntryGuard>();
        TradingCopilotDbContext accountDb = accountScope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();

        await using AsyncServiceScope chatScope = _factory.Services.CreateAsyncScope();
        IChatTurnGuard chatGuard = chatScope.ServiceProvider.GetRequiredService<IChatTurnGuard>();
        TradingCopilotDbContext chatDb = chatScope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();

        bool chatTurnActuallyRan = false;
        int chatResult = -99;

        // AccountEntryGuard's lock is BLOCKING (pg_advisory_lock) and held for the entire callback below — so the
        // chat-turn guard's try-lock, run from a WHOLLY SEPARATE connection while this callback is still executing,
        // observes it while the account lock is genuinely, unambiguously held.
        await accountGuard.RunExclusiveAsync(accountDb, sharedId, async () =>
        {
            chatResult = await chatGuard.TryRunExclusiveAsync(
                chatDb,
                sharedId,
                turn: () =>
                {
                    chatTurnActuallyRan = true;
                    return Task.FromResult(1);
                },
                onBusy: () => -1,
                CancellationToken.None);
            return 0;
        }, CancellationToken.None);

        chatResult.Should().Be(
            1,
            "the chat-turn guard's two-argument advisory-lock space must be disjoint from the account-entry "
            + "guard's one-argument space — an account entry held under the account lock must NEVER refuse a chat "
            + "turn, even on the identical id");
        chatTurnActuallyRan.Should().BeTrue(
            "the chat turn must have genuinely RUN (not merely reported a stale result) while the account lock was "
            + "held");
    }

    // =================================================================================================================
    // Helpers.
    // =================================================================================================================

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_json);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    private async Task<Guid> StartConversationAsync(HttpClient client)
    {
        using HttpResponseMessage created = await client.PostAsJsonAsync(
            "/conversations", new CreateConversationRequest($"contention-{Guid.NewGuid():N}"));
        created.StatusCode.Should().Be(HttpStatusCode.OK, "an authenticated operator may start a conversation");
        return (await ReadAsync<ConversationResponse>(created)).Id;
    }

    private Task<HttpResponseMessage> TakeTurnAsync(HttpClient client, Guid conversationId, string content) =>
        client.PostAsJsonAsync($"/conversations/{conversationId}/turns", new ChatTurnRequest(content));

    private async Task<ConversationDetailResponse> ReadConversationAsync(HttpClient client, Guid conversationId)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri($"/conversations/{conversationId}", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadAsync<ConversationDetailResponse>(response);
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        T? value = await response.Content.ReadFromJsonAsync<T>(_json);
        ArgumentNullException.ThrowIfNull(value);
        return value;
    }
}
