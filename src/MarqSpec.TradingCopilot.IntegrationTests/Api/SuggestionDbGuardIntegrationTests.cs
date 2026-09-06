using FluentAssertions;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage (real Postgres, real migrations) for the <b>four database guards on the suggestion spine</b>
/// (gh#552, sub-issue of gh#17; R-4 / R-14). Each is a fail-safe the DbContext and a migration declare but that no
/// test exercised: the three check constraints
/// <c>CK_Suggestions_Mode_NotUndeclared</c> / <c>_State_NotUnknown</c> / <c>_Size_Positive</c>, and the cross-table
/// constraint trigger <c>ct_suggestions_mode_matches_account</c>.
/// </summary>
/// <remarks>
/// These are guards <b>by construction</b> (the QA contract's preferred kind): the assertion is that the database
/// itself refuses the row, so no future insert path can bypass them however it is written. The suite drives the
/// real <see cref="PostgresApiFactory"/> pipeline, whose <c>MigrateAsync()</c> makes the constraints and the trigger
/// live in the container, and inserts through the production <see cref="TradingCopilotDbContext"/> — never
/// <c>EnsureCreated</c>. Each test isolates <b>one</b> guard: the seeded account's mode is chosen so the deliberate
/// violation trips the constraint under test and no other (e.g. an undeclared-mode row is inserted onto an
/// undeclared account, so the mode-match trigger is satisfied and only the check fires). Traces R-4 · R-14 · gh#552.
/// </remarks>
public sealed class SuggestionDbGuardIntegrationTests
    : IClassFixture<SuggestionGuardsTestPostgresFactory>
{
    private readonly SuggestionGuardsTestPostgresFactory _factory;

    public SuggestionDbGuardIntegrationTests(SuggestionGuardsTestPostgresFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Insert_IsRejected_WhenModeUndeclared()
    {
        // Seeded onto an UNDECLARED account so the mode-match trigger is satisfied (undeclared == undeclared) and the
        // failure can only be the check constraint under test — an undeclared account is tradeable nowhere, so a
        // suggestion must never carry that mode regardless of which account it names.
        (Guid accountId, Guid operatorId) = await SeedAccountAsync(TradingMode.Undeclared);
        Suggestion invalid = ValidSuggestion(accountId, operatorId, TradingMode.Undeclared);

        (await Inserting(invalid).Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be("CK_Suggestions_Mode_NotUndeclared");
    }

    [Fact]
    public async Task Insert_IsRejected_WhenStateUnknown()
    {
        (Guid accountId, Guid operatorId) = await SeedAccountAsync(TradingMode.Practice);
        Suggestion invalid = ValidSuggestion(accountId, operatorId, TradingMode.Practice);
        invalid.State = SuggestionState.Unknown;

        (await Inserting(invalid).Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be("CK_Suggestions_State_NotUnknown");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Insert_IsRejected_WhenSizeNotPositive(int size)
    {
        (Guid accountId, Guid operatorId) = await SeedAccountAsync(TradingMode.Practice);
        Suggestion invalid = ValidSuggestion(accountId, operatorId, TradingMode.Practice);
        invalid.Size = size;

        (await Inserting(invalid).Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be("CK_Suggestions_Size_Positive");
    }

    [Fact]
    public async Task Insert_IsRejected_WhenModeDisagreesWithAccount()
    {
        // The cross-table R-14 guard: a check constraint cannot read another table, so this is a constraint trigger
        // that raises rather than a named constraint. Both modes are valid (non-undeclared), so only the mismatch
        // trips — the account is Practice, the suggestion claims Live.
        (Guid accountId, Guid operatorId) = await SeedAccountAsync(TradingMode.Practice);
        Suggestion mismatched = ValidSuggestion(accountId, operatorId, TradingMode.Live);

        PostgresException pg = (await Inserting(mismatched).Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>().Which;

        pg.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
        pg.MessageText.Should().Contain("R-14 mode guard");
    }

    [Fact]
    public async Task Insert_Succeeds_WhenEveryGuardIsSatisfied()
    {
        // The positive control the guard tests need: prove the seed graph and a well-formed suggestion are otherwise
        // accepted, so a rejection above is the specific violation and not a broken fixture.
        (Guid accountId, Guid operatorId) = await SeedAccountAsync(TradingMode.Practice);
        Suggestion valid = ValidSuggestion(accountId, operatorId, TradingMode.Practice);

        await Inserting(valid).Should().NotThrowAsync();

        // Inserting without throwing is only half of "well-formed" — the row must also be OWNED. Guid.Empty is not
        // a neutral placeholder here: the data layer reserves it as the fail-closed "no user context ⇒ read
        // nothing" sentinel (ICurrentUser, and SystemOwner's own remarks), so a real row carrying it is readable by
        // any context with no user at all. An unowned suggestion therefore inserts perfectly happily and still
        // fails to be the well-formed row this positive control claims to license the other four guards with.
        //
        // Read back with IgnoreQueryFilters and assert the owner explicitly, rather than relying on a filtered read
        // to surface it: this suite's scopes carry no request, so the ambient filter is UserId == Guid.Empty, and a
        // filtered read would pass on the ORPHAN and fail on the correctly-owned row — backwards.
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();

        Suggestion stored = await database.Suggestions.IgnoreQueryFilters()
            .SingleAsync(suggestion => suggestion.Id == valid.Id);

        stored.UserId.Should().Be(operatorId, "a well-formed suggestion is owned by the operator whose account it names (R-20)");
        stored.UserId.Should().NotBe(Guid.Empty, "Guid.Empty is the fail-closed no-user sentinel, not an owner");
    }

    /// <summary>
    /// Seeds a firm → connection → account graph, the account carrying <paramref name="mode"/>.
    /// </summary>
    /// <returns>
    /// The account, <b>and the operator that owns it</b>. The owner is returned rather than kept private because
    /// every row this suite inserts has to carry it: the schema is per-user and fail-closed (R-20, ADR-0017), so a
    /// suggestion left on <c>Guid.Empty</c> is an orphan no legitimate query would surface — which would make the
    /// positive control prove that an *orphaned* row inserts, not a well-formed one.
    /// </returns>
    private async Task<(Guid AccountId, Guid OperatorId)> SeedAccountAsync(TradingMode mode)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();

        Guid operatorId = (await database.Users.IgnoreQueryFilters().FirstAsync()).Id;
        Firm firm = new()
        {
            Id = Guid.NewGuid(),
            UserId = operatorId,
            Name = $"Firm-{Guid.NewGuid():N}",
            Type = FirmType.PropFirm,
        };
        Connection connection = new()
        {
            Id = Guid.NewGuid(),
            UserId = operatorId,
            FirmId = firm.Id,
            Platform = "projectx",
            CredentialKey = $"TEST_KEY_{Guid.NewGuid():N}",
        };
        Account account = new()
        {
            Id = Guid.NewGuid(),
            UserId = operatorId,
            ConnectionId = connection.Id,
            VenueAccountKey = $"V-{Guid.NewGuid():N}",
            Name = "Guard test account",
            Mode = mode,
            CanTrade = true,
            IsVisible = true,
            Balance = 1_000m,
        };

        database.Firms.Add(firm);
        database.Connections.Add(connection);
        database.Accounts.Add(account);
        await database.SaveChangesAsync();
        return (account.Id, operatorId);
    }

    /// <summary>
    /// A fully-specified, guard-satisfying suggestion for <paramref name="accountId"/> in <paramref name="mode"/>,
    /// owned by <paramref name="operatorId"/> — the same operator the seeded firm, connection and account carry.
    /// </summary>
    private static Suggestion ValidSuggestion(Guid accountId, Guid operatorId, TradingMode mode) => new()
    {
        Origin = SuggestionOrigin.Scan,
        Id = Guid.NewGuid(),
        AccountId = accountId,
        UserId = operatorId,
        Instrument = "ES",
        Side = OrderSide.Buy,
        Size = 1,
        EntryPrice = 5_000m,
        StopPrice = 4_990m,
        TargetPrice = 5_020m,
        Mode = mode,
        State = SuggestionState.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        Rationale = "A test setup.",
        CitedFactors =
        [
            new CitedFactor
            {
                Id = Guid.NewGuid(),
                UserId = operatorId,
                Kind = CitedFactorKind.Indicator,
                IsPrimary = true,
                TimeframeMinutes = 5,
                Indicator = "EMA",
                Period = 20,
            },
        ],
        Confidence = 50,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
    };

    private Func<Task> Inserting(Suggestion suggestion) => async () =>
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        database.Suggestions.Add(suggestion);
        await database.SaveChangesAsync();
    };
}
