using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarqSpec.TradingCopilot.Api.Auth;
using MarqSpec.TradingCopilot.Api.Firms;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Venue;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// Integration coverage for the <b>venue-truth resting-orders read</b> — <c>GET /accounts/{id}/orders</c>
/// (gh#534 ⇒ gh#381, R-13, R-20, ADR-0013).
/// </summary>
/// <remarks>
/// <para>
/// This is the operator's answer to <i>is protection actually standing at the venue?</i>, and every claim it makes
/// is computed in the endpoint projection — <c>isProtective</c> exists nowhere in the domain types, so it can only
/// be asserted over HTTP.
/// </para>
/// <para>
/// The unit tier does not stand behind it: its endpoint tests cover the 404 and then construct a
/// <c>WorkingOrder</c> locally and assert <c>StopPrice is not null</c> on it — a tautology that never calls the
/// handler. So the size, the protective classification and the declared-unknown basis had no coverage that could
/// fail on them.
/// </para>
/// </remarks>
public class WorkingOrderReadIntegrationTests : IClassFixture<WorkingOrderReadTestPostgresFactory>
{
    private readonly WorkingOrderReadTestPostgresFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public WorkingOrderReadIntegrationTests(WorkingOrderReadTestPostgresFactory factory)
    {
        _factory = factory;
    }

    private const string VenueKey = "PRAC-50K-101";
    private const string Contract = "MESU26";

    [Fact]
    public async Task Read_ShouldCarryEachRestingLegsSize_OverHttp()
    {
        // gh#381's reason for existing: a protective leg sized to LESS than the position it guards leaves the
        // remainder unprotected, and that was invisible through the app. Two legs at DIFFERENT sizes, neither of
        // them 1 — a projection that drops size, or hard-codes a lot count, cannot satisfy both.
        (HttpClient client, Guid accountId) = await FreshAsync();
        SeedBracket();

        using HttpResponseMessage response = await client.GetAsync($"/accounts/{accountId}/orders");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument body = await BodyAsync(response);
        JsonElement orders = body.RootElement.GetProperty("orders");
        orders.GetArrayLength().Should().Be(2);

        Leg(orders, "STOP-1").GetProperty("size").GetInt32().Should().Be(3,
            "the size the venue reports is the field this read exists to surface");
        Leg(orders, "STOP-1").GetProperty("stopPrice").GetDecimal().Should().Be(4_980m);
        Leg(orders, "TP-1").GetProperty("size").GetInt32().Should().Be(2,
            "each leg carries its OWN size — not a shared constant");

        body.RootElement.GetProperty("markBasis").GetString().Should().NotBe("Unknown",
            "the venue answered. Live vs Settlement is NOT assertable — the endpoint reads UtcNow, and the "
            + "built-in schedules make Settlement legitimate for hours of every day");
    }

    [Fact]
    public async Task Read_ShouldMarkOnlyTheStopLegProtective_WhenALimitOnlyLegRestsBesideIt()
    {
        // A take-profit rests on the WINNING side: it caps the gain and protects nothing about the loss. Reporting
        // it as protection would show cover that is not there — the worst direction for this particular read.
        (HttpClient client, Guid accountId) = await FreshAsync();
        SeedBracket();

        using HttpResponseMessage response = await client.GetAsync($"/accounts/{accountId}/orders");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument body = await BodyAsync(response);
        JsonElement orders = body.RootElement.GetProperty("orders");

        Leg(orders, "STOP-1").GetProperty("isProtective").GetBoolean().Should().BeTrue(
            "a leg carrying a stop trigger is protection");
        Leg(orders, "STOP-1").GetProperty("limitPrice").ValueKind.Should().Be(JsonValueKind.Null);

        Leg(orders, "TP-1").GetProperty("isProtective").GetBoolean().Should().BeFalse(
            "a limit-only take-profit protects nothing about the loss");
        Leg(orders, "TP-1").GetProperty("stopPrice").ValueKind.Should().Be(JsonValueKind.Null);
        Leg(orders, "TP-1").GetProperty("limitPrice").GetDecimal().Should().Be(5_020m);
    }

    [Fact]
    public async Task Read_ShouldDistinguishDeclaredUnknownFromAnEmptyBook_AtTheWire()
    {
        // The whole value of this read. Asked "is protection standing?", "we could not find out" and "nothing is
        // there" are OPPOSITE answers, and rendering the first as the second is the dangerous direction — it reads
        // as nothing to worry about.
        //
        // `orders` is [] in BOTH states, so `markBasis` is the only discriminator. Both legs live in ONE test
        // deliberately: a test asserting only "empty" on the unreachable leg would pass against a system that
        // reports every failure as an empty book, which is precisely the defect.
        (HttpClient client, Guid accountId) = await FreshAsync();

        using HttpResponseMessage reachable = await client.GetAsync($"/accounts/{accountId}/orders");
        reachable.StatusCode.Should().Be(HttpStatusCode.OK);
        string reachableBody = await reachable.Content.ReadAsStringAsync();
        using (JsonDocument empty = JsonDocument.Parse(reachableBody))
        {
            empty.RootElement.GetProperty("orders").GetArrayLength().Should().Be(0,
                "nothing was seeded — the book is genuinely empty");
            empty.RootElement.GetProperty("markBasis").GetString().Should().NotBe("Unknown",
                "the venue was reached, so the read is trustworthy");
        }

        // Fails at the RESTING-ORDERS read specifically. MakeVenueUnreachable would throw from the roster read
        // instead — a coarser claim, and one that also breaks account discovery.
        VenueFactory.MakeAccountUnreadable(VenueKey);

        using HttpResponseMessage unreadable = await client.GetAsync($"/accounts/{accountId}/orders");
        unreadable.StatusCode.Should().Be(HttpStatusCode.OK,
            "declared-unknown is a 200 carrying a basis, not an error status");
        string unreadableBody = await unreadable.Content.ReadAsStringAsync();
        using (JsonDocument unknown = JsonDocument.Parse(unreadableBody))
        {
            unknown.RootElement.GetProperty("markBasis").GetString().Should().Be("Unknown",
                "a read that could not reach venue truth is DECLARED unknown, never presented as a book");
            unknown.RootElement.GetProperty("orders").GetArrayLength().Should().Be(0,
                "declared-unknown carries no orders — it makes no claim at all");
        }

        // Stated as an invariant so no future refactor can quietly collapse the two states. Neither response
        // carries a timestamp or ordering, so the comparison is stable.
        unreadableBody.Should().NotBe(reachableBody,
            "an unreachable venue and an empty book must be TELLABLE APART at the wire — they are opposite answers");
    }

    [Fact]
    public async Task Read_ShouldReturn404ForAnotherOperator_NotAnEmptyList()
    {
        // R-20 default-deny. A 200 with `orders: []` would be the specific failure: it discloses that the id is
        // real AND reads as "no protection standing" on an account the caller cannot see.
        (HttpClient operatorA, Guid accountId) = await FreshAsync();
        VenueFactory.SeedWorkingOrder(VenueKey, "STOP-1", Contract, stopPrice: 4_980m, size: 3);
        HttpClient operatorB = await CreateSecondOperatorAsync(operatorA);

        using HttpResponseMessage mine = await operatorA.GetAsync($"/accounts/{accountId}/orders");
        using HttpResponseMessage theirs = await operatorB.GetAsync($"/accounts/{accountId}/orders");

        mine.StatusCode.Should().Be(HttpStatusCode.OK, "the positive control — the account really does resolve");
        theirs.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "to another operator the account does not exist; an empty 200 would both leak that the id is real and "
            + "read as an absence of protection");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    private AdversarialTestProjectXVenueFactory VenueFactory =>
        _factory.Services.GetRequiredService<AdversarialTestProjectXVenueFactory>();

    /// <summary>A protective stop over 3 lots and a limit-only take-profit over 2 — different sizes on purpose.</summary>
    private void SeedBracket()
    {
        VenueFactory.SeedWorkingOrder(VenueKey, "STOP-1", Contract, stopPrice: 4_980m, size: 3);
        VenueFactory.SeedWorkingOrder(VenueKey, "TP-1", Contract, stopPrice: null, limitPrice: 5_020m, size: 2);
    }

    private static JsonElement Leg(JsonElement orders, string venueOrderKey) => orders
        .EnumerateArray()
        .Single(order => order.GetProperty("venueOrderKey").GetString() == venueOrderKey);

    private static async Task<JsonDocument> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    /// <summary>
    /// Resets the venue and stands up one account. <c>ResetPositions</c> must run first: it clears the seeded
    /// legs <b>and</b> the unreadable-account set, so a previous test's fault injection cannot leak in.
    /// </summary>
    private async Task<(HttpClient Client, Guid AccountId)> FreshAsync()
    {
        VenueFactory.ResetPositions();
        await ExecuteDbAsync(db => db.Accounts.IgnoreQueryFilters().ExecuteDeleteAsync());

        HttpClient client = await AuthenticatedClientAsync();
        return (client, await SetupAccountAsync(client));
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest(PostgresApiFactory.OperatorEmail, PostgresApiFactory.OperatorPassword));
        LoginTokenResponse? auth = await response.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    private async Task<Guid> SetupAccountAsync(HttpClient client)
    {
        using HttpResponseMessage createFirm = await client.PostAsJsonAsync(
            "/firms", new CreateFirmRequest($"Topstep-RestingRead-{Guid.NewGuid():N}", FirmType.PropFirm));
        FirmResponse? firm = await createFirm.Content.ReadFromJsonAsync<FirmResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(firm);

        using HttpResponseMessage conventions = await client.PutAsJsonAsync(
            $"/firms/{firm.Id}/conventions",
            new DeclareConventionsRequest([new StageConventionDto(AccountStage.Practice, CapitalAtRisk: false)]));
        conventions.EnsureSuccessStatusCode();

        // The credential key MUST match the process's own, or the reconciliation short-circuits to Unknown without
        // ever asking the venue — and every case here would pass or fail for the wrong reason.
        using HttpResponseMessage createConn = await client.PostAsJsonAsync(
            "/connections", new CreateConnectionRequest(firm.Id, "projectx", "topstep-main"));
        ConnectionResponse? connection = await createConn.Content.ReadFromJsonAsync<ConnectionResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(connection);

        using HttpResponseMessage discover = await client.PostAsync(
            $"/connections/{connection.Id}/accounts/discover", content: null);
        discover.EnsureSuccessStatusCode();
        List<AccountResponse>? accounts = await discover.Content.ReadFromJsonAsync<List<AccountResponse>>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(accounts);

        return accounts.First(account => account.VenueAccountKey == VenueKey).Id;
    }

    private async Task<HttpClient> CreateSecondOperatorAsync(HttpClient operatorClient)
    {
        string email = $"operator-b-{Guid.NewGuid():N}@example.com";

        using HttpResponseMessage issue = await operatorClient.PostAsJsonAsync(
            "/auth/invitations", new IssueInvitationRequest(email));
        issue.StatusCode.Should().Be(HttpStatusCode.OK, "the primary operator may issue invitations");
        IssueInvitationResponse? invite = await issue.Content.ReadFromJsonAsync<IssueInvitationResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(invite);

        HttpClient client = _factory.CreateClient();
        using HttpResponseMessage accept = await client.PostAsJsonAsync(
            "/auth/accept-invite", new AcceptInviteRequest(invite.Token, "OperatorB-Pass123!", "Operator B"));
        accept.StatusCode.Should().Be(HttpStatusCode.OK);
        LoginTokenResponse? token = await accept.Content.ReadFromJsonAsync<LoginTokenResponse>(_jsonOptions);
        ArgumentNullException.ThrowIfNull(token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return client;
    }

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext database = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(database);
    }

    private sealed record LoginTokenResponse(string Token);

    private sealed record IssueInvitationResponse(Guid Id, string Token, DateTimeOffset ExpiresUtc);
}
