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
        Guid accountId = await SeedAccountAsync(TradingMode.Undeclared);
        Suggestion invalid = ValidSuggestion(accountId, TradingMode.Undeclared);

        (await Inserting(invalid).Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be("CK_Suggestions_Mode_NotUndeclared");
    }

    [Fact]
    public async Task Insert_IsRejected_WhenStateUnknown()
    {
        Guid accountId = await SeedAccountAsync(TradingMode.Practice);
        Suggestion invalid = ValidSuggestion(accountId, TradingMode.Practice);
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
        Guid accountId = await SeedAccountAsync(TradingMode.Practice);
        Suggestion invalid = ValidSuggestion(accountId, TradingMode.Practice);
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
        Guid accountId = await SeedAccountAsync(TradingMode.Practice);
        Suggestion mismatched = ValidSuggestion(accountId, TradingMode.Live);

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
        Guid accountId = await SeedAccountAsync(TradingMode.Practice);
        Suggestion valid = ValidSuggestion(accountId, TradingMode.Practice);

        await Inserting(valid).Should().NotThrowAsync();
    }

    /// <summary>Seeds a firm → connection → account graph, the account carrying <paramref name="mode"/>.</summary>
    private async Task<Guid> SeedAccountAsync(TradingMode mode)
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
        return account.Id;
    }

    /// <summary>A fully-specified, guard-satisfying suggestion for <paramref name="accountId"/> in <paramref name="mode"/>.</summary>
    private static Suggestion ValidSuggestion(Guid accountId, TradingMode mode) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
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
        CitedIndicator = "EMA",
        CitedPeriod = 20,
        CitedResolutionMinutes = 5,
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
