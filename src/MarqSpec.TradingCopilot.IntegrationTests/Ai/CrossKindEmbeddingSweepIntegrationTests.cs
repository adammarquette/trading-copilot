using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace MarqSpec.TradingCopilot.IntegrationTests.Ai;

/// <summary>
/// Independent QA for <b>gh#1096</b> (the paired QA card for gh#1065, build PR #1095) — the embedding GC over the two
/// owner kinds that increment added: <see cref="EmbeddingOwnerKind.Suggestion"/> (anti-joined against
/// <c>Suggestions.Id</c>) and <see cref="EmbeddingOwnerKind.JournalEntry"/> (against <c>Trades.Id</c>), plus the
/// gh#915 stale-model backstop over those same kinds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this cannot live at the unit tier.</b> Both branches are a single
/// <see cref="Microsoft.EntityFrameworkCore.RelationalQueryableExtensions"/> <c>ExecuteDeleteAsync</c> carrying a
/// <c>NOT EXISTS</c> anti-join, and the polymorphic <c>Embeddings</c> store's <c>Vector</c> column has no
/// in-memory-provider mapping at all (gh#109, ADR-0001) — the entity is <c>Ignore()</c>d off anything but Postgres.
/// There is therefore no tier below this one on which either delete can be executed even once.
/// </para>
/// <para>
/// <b>The failure mode this suite exists for.</b> The two new anti-joins compare
/// <c>suggestion.Id.ToString() == embedding.OwnerId</c> — a <c>uuid</c>→<c>text</c> translation with <b>no other
/// precedent in this codebase</b>. If Npgsql renders that cast as anything but the lowercase-hyphenated form
/// <see cref="System.Guid.ToString()"/> wrote on the embed side, every owner-scoped row looks orphaned, the first
/// pass deletes <b>the entire kind</b>, and <i>nothing faults</i> — so
/// <c>EmbeddingOrphanGcHost</c>'s per-kind <c>catch</c> never fires, the next embed pass silently re-embeds and
/// re-bills, and grounding degrades back to news-only with no signal anywhere. A "did the orphan go away" test alone
/// would pass against exactly that defect; every case below therefore carries its <b>live-owner</b> converse, and
/// <see cref="Sweep_ShouldReclaimAnEmbeddingKeyedByANonCanonicalGuidRendering_ProvingTheAntiJoinComparesExactText"/>
/// is the anti-vacuity control proving the comparison is a real string match rather than a predicate that keeps
/// everything.
/// </para>
/// <para>
/// <b>The fixture cost gh#1096 was carded to carry.</b> Both new producers are FK'd to <c>Account</c>, which is FK'd
/// to <c>Connection</c> → <c>Firm</c>, so a genuine live owner needs the whole chain built — which the sibling
/// <see cref="EmbeddingOrphanSweepIntegrationTests"/> fixture (news / topics only) does not build, and which is why
/// its gh#1065 alignment could only <i>drop</i> its <c>Suggestion</c> case rather than convert it. Every live owner
/// here is a real row behind a real chain, and every orphan here is produced by <b>deleting a row that existed</b>
/// rather than by inventing a key that never did.
/// </para>
/// <para>
/// <b><c>IgnoreQueryFilters</c> is load-bearing, and this is the only tier that can witness it.</b> The GC host runs
/// with no request user, so <c>ICurrentUser.UserId</c> is <see cref="System.Guid.Empty"/> and the R-20 default-deny
/// filter on <see cref="Suggestion"/> / <see cref="Trade"/> matches <b>nothing</b> — every row would look orphaned.
/// <see cref="Sweep_ShouldNeverTouchALiveOwnersRow_WhenTheOwnerBelongsToAnotherOperator"/> proves the bypass is real
/// by seeding owners under two <i>different</i> non-empty operators and asserting, in the same case, that a read of
/// the very same tables — scoped to the identity a background DI scope actually resolves, not to an assumed
/// <see cref="System.Guid.Empty"/> — returns zero rows. That control is what stops the assertion passing for the
/// wrong reason, and resolving the identity rather than hard-coding it is what keeps it describing the sweep if a
/// harness ever registers a fixed test user.
/// </para>
/// <para>
/// <b>A swallowed fault is never mistaken for the guarantee.</b> <c>SweepAsync</c> catches per-kind and
/// per-backstop exceptions and keeps going, so an arm that <i>threw</i> reports exactly the zero a correct no-op
/// does — and every "nothing was deleted" case here would pass over it. <see cref="RunSweepAsync"/> therefore
/// drives the sweep through a <see cref="CapturingLogger"/> and asserts nothing warning-or-worse was logged.
/// </para>
/// <para>
/// <b>Every case drives <c>EmbeddingOrphanGcHost.SweepAsync</c></b>, not the store in isolation — that static method
/// is the one place <see cref="EmbeddingOrphanSweep.SweepableKinds"/> is consulted, so it is the only entry point
/// that proves the two new kinds are actually <i>in</i> the sweep rather than merely accepted by the store's switch.
/// A fresh DI scope per pass, exactly as the host does in production.
/// </para>
/// <para>
/// <b>Prove-red (gh#1096, recorded in the PR body).</b> Each guard was run against a deliberately broken local copy
/// of production and confirmed red for its own reason, then restored: dropping the <c>Suggestion</c> /
/// <c>JournalEntry</c> arms' anti-join (an unconditional delete) reddens <b>all six</b> cases — not only the
/// live-owner ones, since the two stale-model cases seed live owners too; making them no-ops
/// reddens every orphan case; removing <c>IgnoreQueryFilters</c> from both arms reddens <b>every</b> case, not only
/// the cross-operator one — with no request user the producer look-up matches nothing, so the first pass deletes
/// each kind entire (its <i>inner</i> repetition on the producer sub-query, by contrast, is inert: removing that
/// alone leaves all six green, because the root call is query-wide); removing the two new kinds from
/// <see cref="EmbeddingOrphanSweep.SweepableKinds"/> reddens
/// the orphan cases (nothing is swept at all); and dropping the stale-model self-<c>EXISTS</c>, or making that
/// backstop a no-op, reddens exactly one of the two stale-model cases each. The broken copies were never
/// committed.
/// </para>
/// </remarks>
public sealed class CrossKindEmbeddingSweepIntegrationTests : IClassFixture<EmbeddingReadTestPostgresFactory>
{
    private const string ModelA = "gh1096-cross-kind-model-a";
    private const string ModelB = "gh1096-cross-kind-model-b";

    private static readonly DateTimeOffset _recordedAt = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly EmbeddingReadTestPostgresFactory _factory;

    public CrossKindEmbeddingSweepIntegrationTests(EmbeddingReadTestPostgresFactory factory)
    {
        _factory = factory;
        // One container per suite (gh#121), shared across the class: start every case from empty rather than
        // accumulating rows, mirroring the sibling gh#914 / gh#920 suites over the same factory.
        ResetAsync().GetAwaiter().GetResult();
    }

    // =================================================================================================================
    // Scope bullet 1 — the orphaned-owner sweep over Suggestion, in BOTH directions.
    // =================================================================================================================

    [Fact]
    public async Task Sweep_ShouldReclaimAnOrphanedSuggestionEmbedding_AndLeaveALiveSuggestionsRowUntouched()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountChainAsync(owner);
        Guid live = await SeedSuggestionAsync(owner, accountId, "gh#1096 live suggestion");
        Guid deleted = await SeedSuggestionAsync(owner, accountId, "gh#1096 suggestion that will be deleted");

        await SeedEmbeddingAsync(EmbeddingOwnerKind.Suggestion, live.ToString(), ModelA);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Suggestion, deleted.ToString(), ModelA);

        // The orphan is produced the way production produces one -- by removing a row that DID exist -- not by
        // inventing an id that never did.
        await DeleteSuggestionAsync(deleted);

        int swept = await RunSweepAsync();

        swept.Should().Be(1, "exactly the deleted suggestion's embedding has lost its producer");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.Suggestion, deleted.ToString())).Should().BeEmpty(
            "no Suggestions row carries that id any more, so the vector must be reclaimed");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.Suggestion, live.ToString())).Should().ContainSingle(
            "the live suggestion's row must survive -- if the uuid->text anti-join rendered anything but the "
            + "lowercase-hyphenated form the embed pass wrote, THIS is the assertion that catches it, and nothing "
            + "else would: the sweep would silently delete the whole kind without faulting");
    }

    // =================================================================================================================
    // Scope bullet 1 — the same, over JournalEntry (Trades.Id).
    // =================================================================================================================

    [Fact]
    public async Task Sweep_ShouldReclaimAnOrphanedJournalEntryEmbedding_AndLeaveALiveTradesRowUntouched()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountChainAsync(owner);
        Guid live = await SeedClosedTradeAsync(owner, accountId, realizedPnL: 125m);
        Guid deleted = await SeedClosedTradeAsync(owner, accountId, realizedPnL: -75m);

        await SeedEmbeddingAsync(EmbeddingOwnerKind.JournalEntry, live.ToString(), ModelA);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.JournalEntry, deleted.ToString(), ModelA);

        await DeleteTradeAsync(deleted);

        int swept = await RunSweepAsync();

        swept.Should().Be(1, "exactly the deleted trade's embedding has lost its producer");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.JournalEntry, deleted.ToString())).Should().BeEmpty(
            "no Trades row carries that id any more, so the vector must be reclaimed");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.JournalEntry, live.ToString())).Should().ContainSingle(
            "the live closed trade's row must survive -- the dangerous direction of the same uuid->text anti-join");
    }

    // =================================================================================================================
    // Scope bullet 1, anti-vacuity control — the anti-join really compares TEXT, exactly.
    //
    // The two live-owner cases above pass if the comparison were, say, always-true. This case pins the converse: an
    // OwnerId that is the SAME Guid in a DIFFERENT rendering (uppercase, and the hyphen-less "N" form) has no
    // producer as far as the anti-join is concerned and IS reclaimed, while the canonical row beside it survives.
    // Not a defect and not a behaviour to preserve for its own sake -- production only ever writes Guid.ToString(),
    // so this is the control that makes the live-owner assertions mean something.
    // =================================================================================================================

    [Fact]
    public async Task Sweep_ShouldReclaimAnEmbeddingKeyedByANonCanonicalGuidRendering_ProvingTheAntiJoinComparesExactText()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountChainAsync(owner);
        Guid live = await SeedSuggestionAsync(owner, accountId, "gh#1096 rendering control");

        await SeedEmbeddingAsync(EmbeddingOwnerKind.Suggestion, live.ToString(), ModelA);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Suggestion, live.ToString().ToUpperInvariant(), ModelA);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Suggestion, live.ToString("N"), ModelA);

        int swept = await RunSweepAsync();

        swept.Should().Be(2, "the two non-canonical renderings match no producer; the canonical one does");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.Suggestion, live.ToString())).Should().ContainSingle(
            "the canonically-keyed row -- the only shape the embed pass ever writes -- is live and stays");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.Suggestion, live.ToString().ToUpperInvariant())).Should().BeEmpty(
            "an uppercase rendering is a genuinely different text value, so the anti-join finds no producer -- which "
            + "is what proves the predicate is a real comparison and not one that keeps every row");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.Suggestion, live.ToString("N"))).Should().BeEmpty(
            "same, for the hyphen-less form -- Postgres renders a uuid hyphenated, so this cannot match either");
    }

    // =================================================================================================================
    // Scope bullet 2 — IgnoreQueryFilters is genuinely load-bearing on both new branches.
    //
    // The GC host has no request user, so ICurrentUser.UserId is Guid.Empty and the R-20 default-deny filter on
    // Suggestion / Trade matches NOTHING. Without the bypass, every owner-scoped row looks orphaned and the first
    // pass deletes the entire kind. Both owners here are non-empty and DIFFERENT from each other, so neither could
    // be reachable by accident; the filtered-read control in the same case proves the filter would really bite.
    // =================================================================================================================

    [Fact]
    public async Task Sweep_ShouldNeverTouchALiveOwnersRow_WhenTheOwnerBelongsToAnotherOperator()
    {
        Guid operatorOne = Guid.NewGuid();
        Guid operatorTwo = Guid.NewGuid();
        Guid accountOne = await SeedAccountChainAsync(operatorOne);
        Guid accountTwo = await SeedAccountChainAsync(operatorTwo);

        Guid suggestionOne = await SeedSuggestionAsync(operatorOne, accountOne, "gh#1096 operator one");
        Guid suggestionTwo = await SeedSuggestionAsync(operatorTwo, accountTwo, "gh#1096 operator two");
        Guid tradeOne = await SeedClosedTradeAsync(operatorOne, accountOne, realizedPnL: 40m);
        Guid tradeTwo = await SeedClosedTradeAsync(operatorTwo, accountTwo, realizedPnL: -40m);

        await SeedEmbeddingAsync(EmbeddingOwnerKind.Suggestion, suggestionOne.ToString(), ModelA);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Suggestion, suggestionTwo.ToString(), ModelA);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.JournalEntry, tradeOne.ToString(), ModelA);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.JournalEntry, tradeTwo.ToString(), ModelA);

        // THE CONTROL. Read the same two tables through the tenancy the GC host ACTUALLY runs under -- resolved from
        // ICurrentUser in a background scope, not hard-coded, so the control keeps describing the sweep even if a
        // base factory ever registers a fixed test user. If these came back non-empty, every assertion below would
        // pass whether the production bypass existed or not -- this is what makes the case a genuine guard on
        // IgnoreQueryFilters rather than on liveness.
        Guid sweepUser = await SweepScopeUserIdAsync();
        sweepUser.Should().NotBe(operatorOne).And.NotBe(
            operatorTwo, "the GC host is not either owner, so neither owner's rows are visible to it by accident");
        (await FilteredSuggestionCountAsync(sweepUser)).Should().Be(
            0, "the background scope has no request user, so the R-20 filter hides every suggestion -- the sweep's "
            + "producer check can only see these rows by crossing the filter deliberately");
        (await FilteredTradeCountAsync(sweepUser)).Should().Be(0, "the same, for the journal's producer table");

        int swept = await RunSweepAsync();

        swept.Should().Be(
            0, "every seeded row's producer still exists -- for SOMEONE. The sweep asks 'does this row exist "
            + "anywhere', a deployment-wide question, so a foreign owner's live row is still a live owner");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.Suggestion, suggestionOne.ToString())).Should().ContainSingle();
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.Suggestion, suggestionTwo.ToString())).Should().ContainSingle();
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.JournalEntry, tradeOne.ToString())).Should().ContainSingle();
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.JournalEntry, tradeTwo.ToString())).Should().ContainSingle();
    }

    // =================================================================================================================
    // Scope bullet 3 — the gh#915 stale-model backstop over the two new kinds.
    //
    // It is owner-kind-agnostic by construction (the self-EXISTS proves legitimacy without a producer lookup), so
    // this is a proof that the property HOLDS for the new kinds rather than a new mechanism. Every owner is LIVE, so
    // the orphaned-owner sweep that runs first in the same SweepAsync call contributes zero to the returned count --
    // otherwise "exactly N stale rows deleted" could not tell the two sweeps apart (the gh#920 discipline).
    // =================================================================================================================

    [Fact]
    public async Task StaleModelBackstop_ShouldDeleteAStaleDuplicate_AndKeepTheCurrentOne_ForBothNewKinds()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountChainAsync(owner);
        Guid suggestionId = await SeedSuggestionAsync(owner, accountId, "gh#1096 stale-model suggestion");
        Guid tradeId = await SeedClosedTradeAsync(owner, accountId, realizedPnL: 10m);

        await SeedEmbeddingAsync(EmbeddingOwnerKind.Suggestion, suggestionId.ToString(), ModelB); // stale
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Suggestion, suggestionId.ToString(), ModelA); // current
        await SeedEmbeddingAsync(EmbeddingOwnerKind.JournalEntry, tradeId.ToString(), ModelB); // stale
        await SeedEmbeddingAsync(EmbeddingOwnerKind.JournalEntry, tradeId.ToString(), ModelA); // current

        int swept = await RunSweepAsync(currentModel: ModelA);

        swept.Should().Be(2, "one crash-leaked stale duplicate per new kind, and nothing else -- every owner is live");

        List<EmbeddingRecord> suggestionRows =
            await ReadEmbeddingsAsync(EmbeddingOwnerKind.Suggestion, suggestionId.ToString());
        suggestionRows.Should().ContainSingle().Which.Model.Should().Be(
            ModelA, "the suggestion keeps exactly its current-model vector");

        List<EmbeddingRecord> tradeRows = await ReadEmbeddingsAsync(EmbeddingOwnerKind.JournalEntry, tradeId.ToString());
        tradeRows.Should().ContainSingle().Which.Model.Should().Be(
            ModelA, "the journal entry keeps exactly its current-model vector");
    }

    [Fact]
    public async Task StaleModelBackstop_ShouldNeverTouchAnOwnersOnlyVector_ForBothNewKinds()
    {
        Guid owner = Guid.NewGuid();
        Guid accountId = await SeedAccountChainAsync(owner);
        Guid suggestionId = await SeedSuggestionAsync(owner, accountId, "gh#1096 sole-vector suggestion");
        Guid tradeId = await SeedClosedTradeAsync(owner, accountId, realizedPnL: -20m);

        // Each owner's ONLY vector, and it is under a model that is no longer current -- exactly the row a
        // model-agnostic "delete everything stale" would destroy, leaving the owner unretrievable until a paid
        // re-embed. The self-EXISTS is what saves it: there is no current-model sibling to make it redundant.
        await SeedEmbeddingAsync(EmbeddingOwnerKind.Suggestion, suggestionId.ToString(), ModelB);
        await SeedEmbeddingAsync(EmbeddingOwnerKind.JournalEntry, tradeId.ToString(), ModelB);

        int swept = await RunSweepAsync(currentModel: ModelA);

        swept.Should().Be(0, "neither owner has a current-model sibling, so neither stale row is a redundant duplicate");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.Suggestion, suggestionId.ToString())).Should().ContainSingle()
            .Which.Model.Should().Be(ModelB, "an owner's sole vector survives whatever model it carries");
        (await ReadEmbeddingsAsync(EmbeddingOwnerKind.JournalEntry, tradeId.ToString())).Should().ContainSingle()
            .Which.Model.Should().Be(ModelB, "the same guarantee for the journal kind");
    }

    // =================================================================================================================
    // Fixture.
    // =================================================================================================================

    private static Vector Zero() => new(new float[TradingCopilotDbContext.EmbeddingDimensions]);

    /// <summary>
    /// Builds the whole owner chain a live suggestion / journal entry needs — <c>Firm</c> → <c>Connection</c> →
    /// <c>Account</c> — under <paramref name="owner"/>. This is the fixture cost gh#1096 was carded to carry: the
    /// sibling sweep suite builds only news and topics, which have no owner at all.
    /// </summary>
    private async Task<Guid> SeedAccountChainAsync(Guid owner)
    {
        Guid firmId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        database.Firms.Add(new Firm { Id = firmId, UserId = owner, Name = "gh1096-firm", Type = FirmType.PropFirm });
        database.Connections.Add(new Connection
        {
            Id = connectionId,
            UserId = owner,
            FirmId = firmId,
            Platform = "projectx",
            CredentialKey = $"k{Guid.NewGuid():N}"[..16],
        });
        database.Accounts.Add(new Account
        {
            Id = accountId,
            UserId = owner,
            ConnectionId = connectionId,
            VenueAccountKey = $"A{Guid.NewGuid():N}"[..16],
            Name = "PRAC-50K",
            Stage = AccountStage.Practice,
            // Practice, matching the suggestions below: ct_suggestions_mode_matches_account (R-14) refuses a
            // suggestion whose mode disagrees with its account's, so the chain is what makes the producer writable.
            Mode = TradingMode.Practice,
            CanTrade = true,
            IsVisible = true,
        });
        await database.SaveChangesAsync();
        return accountId;
    }

    private async Task<Guid> SeedSuggestionAsync(Guid owner, Guid accountId, string rationale)
    {
        Guid suggestionId = Guid.NewGuid();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        database.Suggestions.Add(new Suggestion
        {
            Id = suggestionId,
            UserId = owner,
            AccountId = accountId,
            Instrument = "ESM25",
            Side = OrderSide.Buy,
            Size = 1,
            EntryPrice = 5_000m,
            StopPrice = 4_990m,
            TargetPrice = 5_020m,
            Mode = TradingMode.Practice,
            State = SuggestionState.ExpiredVoid,
            CreatedAt = _recordedAt,
            Rationale = rationale,
            Confidence = 50,
            ExpiresAt = _recordedAt.AddHours(1),
        });
        await database.SaveChangesAsync();
        return suggestionId;
    }

    private async Task<Guid> SeedClosedTradeAsync(Guid owner, Guid accountId, decimal realizedPnL)
    {
        Guid tradeId = Guid.NewGuid();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        database.Trades.Add(new Trade
        {
            Id = tradeId,
            UserId = owner,
            AccountId = accountId,
            Instrument = "ESM25",
            Side = OrderSide.Buy,
            Size = 1,
            EntryPrice = 5_000m,
            ExitPrice = 5_000m + realizedPnL,
            RealizedPnL = realizedPnL,
            Mode = TradingMode.Practice,
            // A JournalEntry is a CLOSED trade (gh#1065): an open one is never embedded, so a fixture that left this
            // null would be arranging a row the producer never creates.
            ClosedAt = _recordedAt,
        });
        await database.SaveChangesAsync();
        return tradeId;
    }

    private async Task DeleteSuggestionAsync(Guid suggestionId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await database.Suggestions.IgnoreQueryFilters()
            .Where(suggestion => suggestion.Id == suggestionId)
            .ExecuteDeleteAsync();
    }

    private async Task DeleteTradeAsync(Guid tradeId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await database.Trades.IgnoreQueryFilters()
            .Where(trade => trade.Id == tradeId)
            .ExecuteDeleteAsync();
    }

    private async Task SeedEmbeddingAsync(EmbeddingOwnerKind ownerKind, string ownerId, string model)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        database.Embeddings.Add(new EmbeddingRecord
        {
            OwnerKind = ownerKind,
            OwnerId = ownerId,
            Model = model,
            Dimensions = TradingCopilotDbContext.EmbeddingDimensions,
            Embedding = Zero(),
            ContentHash = "gh1096-test-content-hash",
            RecordedAt = _recordedAt,
        });
        await database.SaveChangesAsync();
    }

    /// <summary>
    /// Runs one full sweep pass through the real orchestrator over the real, DI-resolved
    /// <see cref="IEmbeddingOrphanStore"/> — a fresh scope per call, exactly as <c>EmbeddingOrphanGcHost</c> does per
    /// pass in production, and the only entry point that consults
    /// <see cref="EmbeddingOrphanSweep.SweepableKinds"/>.
    /// </summary>
    /// <param name="currentModel">
    /// <see langword="null"/> runs the orphaned-owner sweep only (gh#902); a model additionally runs the gh#915
    /// stale-model backstop.
    /// </param>
    private async Task<int> RunSweepAsync(string? currentModel = null)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEmbeddingOrphanStore store = scope.ServiceProvider.GetRequiredService<IEmbeddingOrphanStore>();
        CapturingLogger logger = new();
        int swept = await EmbeddingOrphanGcHost.SweepAsync(store, currentModel, logger, CancellationToken.None);

        // WITHOUT THIS, "0 deleted" is ambiguous. SweepAsync catches per-kind and per-backstop exceptions and keeps
        // going, so a sweep arm that THREW -- a broken translation, a dropped column, an unmapped kind -- reports
        // exactly the same count as an arm that correctly found nothing, and every "nothing was deleted" case here
        // would pass over it. The swallowed fault is only ever visible in the log, so the log is asserted.
        logger.Faults.Should().BeEmpty(
            "a sweep arm that faulted is swallowed by SweepAsync's per-kind catch and reports the same zero a "
            + "correct no-op does -- so a fault must never be mistaken for the guarantee under test");

        return swept;
    }

    private async Task<List<EmbeddingRecord>> ReadEmbeddingsAsync(EmbeddingOwnerKind ownerKind, string ownerId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await database.Embeddings
            .Where(embedding => embedding.OwnerKind == ownerKind && embedding.OwnerId == ownerId)
            .ToListAsync();
    }

    /// <summary>
    /// The user id a background DI scope actually resolves — the tenancy <c>EmbeddingOrphanGcHost</c> runs its sweep
    /// under. Read from the composed <see cref="ICurrentUser"/> rather than assumed, so the cross-operator control
    /// stays true by construction rather than true today.
    /// </summary>
    private async Task<Guid> SweepScopeUserIdAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<ICurrentUser>().UserId;
    }

    /// <summary>How many suggestions a context scoped to <paramref name="userId"/> can see — the R-20 filter, live.</summary>
    private Task<int> FilteredSuggestionCountAsync(Guid userId) =>
        WithScopedContextAsync(userId, scoped => scoped.Suggestions.CountAsync());

    /// <summary>How many trades a context scoped to <paramref name="userId"/> can see — the R-20 filter, live.</summary>
    private Task<int> FilteredTradeCountAsync(Guid userId) =>
        WithScopedContextAsync(userId, scoped => scoped.Trades.CountAsync());

    /// <summary>
    /// Runs <paramref name="read"/> against a context pinned to <paramref name="userId"/> — the R-20 filter as the GC
    /// host really sees it. The DI scope must outlive the context (EF resolves its internal services lazily on first
    /// use), so both live for exactly this call.
    /// </summary>
    private async Task<T> WithScopedContextAsync<T>(Guid userId, Func<TradingCopilotDbContext, Task<T>> read)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        DbContextOptions<TradingCopilotDbContext> options =
            scope.ServiceProvider.GetRequiredService<DbContextOptions<TradingCopilotDbContext>>();
        await using TradingCopilotDbContext scoped = new(options, new FixedUser(userId));
        return await read(scoped);
    }

    private async Task ResetAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await database.Embeddings.ExecuteDeleteAsync();
        await database.Trades.IgnoreQueryFilters().ExecuteDeleteAsync();
        await database.Suggestions.IgnoreQueryFilters().ExecuteDeleteAsync();
        await database.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync();
        await database.Connections.IgnoreQueryFilters().ExecuteDeleteAsync();
        await database.Firms.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    /// <summary>
    /// A logger that keeps every warning/error <c>EmbeddingOrphanGcHost.SweepAsync</c> writes, so a swallowed
    /// per-kind fault cannot masquerade as a correct "nothing to delete".
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly List<string> _faults = [];

        /// <summary>Every warning-or-worse the sweep logged, in order.</summary>
        public IReadOnlyList<string> Faults => _faults;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (logLevel >= LogLevel.Warning)
            {
                _faults.Add($"{logLevel}: {formatter(state, exception)}");
            }
        }
    }
}
