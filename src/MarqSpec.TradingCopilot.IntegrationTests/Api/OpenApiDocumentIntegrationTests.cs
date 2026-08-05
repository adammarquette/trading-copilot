using System.Net;
using System.Text.Json;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace MarqSpec.TradingCopilot.IntegrationTests.Api;

/// <summary>
/// The <b>drift-guard</b> over the generated OpenAPI document (gh#612 ⇒ gh#604, R-10): the test that makes "the
/// spec can never silently drop an endpoint" true rather than merely intended.
/// </summary>
/// <remarks>
/// <para>
/// gh#604 replaced a hand-maintained README endpoint table (#605) with a document generated from the live
/// minimal-API routes, on the grounds that <b>a generated spec is verifiable and a hand-kept table never was</b>.
/// That argument is only worth anything if something actually verifies it — otherwise the failure mode simply
/// moved: instead of a table nobody updated, a document nobody reads, silently missing whatever
/// <c>Program.cs</c> forgot to map.
/// </para>
/// <para>
/// <b>Why this tier.</b> The document only materialises once the host has run through endpoint mapping, which is
/// after <c>StartupTasks.MigrateAndBootstrapAsync</c> — so it needs the real pipeline over a real database, and
/// the unit tier cannot reach it. The pure logic gh#604 shipped (<c>ScalarUiPolicyTests</c>,
/// <c>BearerSecurityTests</c>, <c>OpenApiRegistrationTests</c>) is deliberately <b>not</b> repeated here; this
/// suite asserts only what a client of <c>/openapi/v1.json</c> actually receives.
/// </para>
/// <para>
/// <b>Assertions are made against the raw JSON, not a re-parsed object model.</b> The JSON on the wire is the
/// artefact Scalar and every client generator consume; a model read back through <see cref="OpenApiModelFactory"/>
/// can present a defaulted or resolved value for something the wire never carried, which would let this suite
/// bless a document no client can use. The reader is used for exactly one thing —
/// <see cref="TheSpec_ShouldBeServed_AsAParseableOpenApi3Document"/>, where "does this parse as OpenAPI 3" is the
/// question — and nowhere else. That distinction is not academic here: it is what found the gh#604 defect pinned
/// in <see cref="TheBearerRequirement_ShouldNameTheBearerScheme"/>.
/// </para>
/// </remarks>
public class OpenApiDocumentIntegrationTests : IClassFixture<PostgresApiFactory>
{
    /// <summary>Where the document is served — <c>OpenApiRegistration.DocumentName</c> is <c>v1</c>.</summary>
    private const string SpecPath = "/openapi/v1.json";

    private readonly PostgresApiFactory _factory;
    private readonly HttpClient _client;

    public OpenApiDocumentIntegrationTests(PostgresApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>
    /// One path per endpoint group, with the tag that group carries — the anchor set gh#612 names, so an
    /// accidentally-unmapped group fails rather than quietly shrinking the document.
    /// </summary>
    /// <remarks>
    /// A group is the unit of loss: <c>Program.cs</c> maps each of them with a single
    /// <c>app.MapXEndpoints()</c> line, so one deleted or misordered line takes <b>every</b> route in that group
    /// out of the spec at once. Anchoring one path per group is therefore not a sample — it is one probe per way
    /// the document can lose a whole area of the API.
    /// <para>
    /// <b>The templates are the generator's, not the router's.</b> The route table carries the constraint
    /// (<c>/accounts/{id:guid}/orders</c> — see <see cref="AuthorizationSurfaceIntegrationTests"/>, which reads
    /// it); the OpenAPI generator strips it and emits <c>/accounts/{id}/orders</c>, with the constraint surviving
    /// as the parameter's schema. These are the generator's shapes, verified against the real document.
    /// </para>
    /// </remarks>
    private static Dictionary<string, string> AnchorPaths { get; } = new(StringComparer.Ordinal)
    {
        ["/auth/login"] = "Auth",
        ["/accounts/{id}/orders"] = "Orders",
        ["/accounts/{id}/orders/conditional"] = "Orders",
        ["/orders/{id}/take"] = "Orders",
        ["/kill-switch"] = "Kill Switch",
        ["/accounts/{id}/positions"] = "Positions",
        ["/accounts/{id}/risk"] = "Risk",
        ["/api/triggers"] = "Triggers",
        ["/suggestions/{id}"] = "Suggestions",
        ["/connections"] = "Connections",
    };

    /// <summary>
    /// The only operations that may document themselves as reachable without a token — the mirror image of
    /// <see cref="AuthorizationSurfaceIntegrationTests"/>'s allow-list, and asserted here because a spec that
    /// disagrees with the router sends clients at endpoints that will refuse them (or, far worse, tells them an
    /// execution route is open).
    /// </summary>
    private static string[] OpenOperations { get; } =
    [
        // Declared: [AllowAnonymous], because a caller with no token is exactly who uses them.
        "POST /auth/login",
        "POST /auth/accept-invite",

        // Open by omission: the probes carry no authorization metadata at all (there is no FallbackPolicy).
        "GET /health",
        "GET /ready",
    ];

    /// <summary>
    /// Every heading the Scalar reference UI may show — one per endpoint group, from the group's
    /// <c>MapGroup(...).WithTags(...)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asserted as an exact set, which is the only formulation that works here.</b> A route that never called
    /// <c>.WithTags(...)</c> is not untagged: the generator quietly supplies a default, so a "has at least one
    /// tag" check goes green on precisely the drift it was written to find. Nor is the default a single known
    /// string that could be excluded — it is the <b>handler's declaring type</b>, so an endpoint module that lost
    /// its <c>.WithTags(...)</c> is filed under <c>ConnectionEndpoints</c>, <c>RiskEndpoints</c>, and so on,
    /// one new name per module. Established by deliberately removing <c>.WithTags("Connections")</c> (gh#612
    /// prove-red): the six <c>/connections</c> operations moved to a <c>ConnectionEndpoints</c> heading, and this
    /// test — then written as "every operation has a tag, and only the probes carry the default" — <b>passed</b>.
    /// Pinning the whole set is what makes any of those names fail.
    /// </para>
    /// <para>
    /// The cost is one line here whenever a genuinely new group is added — which is the right price: the set of
    /// headings the API reference presents is small, deliberate, and worth a reviewer's eye.
    /// </para>
    /// </remarks>
    private static string[] GroupTags { get; } =
    [
        "Auth",
        "Firms",
        "Connections",
        "Accounts",
        "Risk",
        "Triggers",
        "Relevance",
        "News",
        "Suggestions",
        "Orders",
        "Kill Switch",
        "Positions",
        "Market data",
    ];

    /// <summary>
    /// The generator's default tag for the two probes: they are lambdas in <c>Program.cs</c>'s top-level
    /// statements, whose declaring type resolves to the assembly name.
    /// </summary>
    private const string ProbeDefaultTag = "MarqSpec.TradingCopilot.Api";

    /// <summary>
    /// Operations that carry <see cref="ProbeDefaultTag"/> because nothing gave them a tag. Named so the set is a
    /// decision on the record rather than an omission.
    /// </summary>
    /// <remarks>
    /// Both are infrastructure probes mapped inline in <c>Program.cs</c> rather than in an endpoint module, so
    /// neither belongs to an API group Scalar would sensibly show; the cost is one stray heading in the reference
    /// UI, which is cosmetic. An <i>endpoint group</i> arriving here would not be — its whole area would leave its
    /// own heading — and would fail the exact-set assertion above regardless.
    /// </remarks>
    private static string[] UntaggedByOmission { get; } =
    [
        "GET /health",
        "GET /ready",
    ];

    // =================================================================================================================
    // The document exists, and is a document
    // =================================================================================================================

    [Fact]
    public async Task TheSpec_ShouldBeServed_AsAParseableOpenApi3Document()
    {
        using HttpResponseMessage response = await _client.GetAsync(SpecPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"{SpecPath} is the endpoint reference the README links to (gh#604, #605) and is served in every "
            + "environment — a 404 here means AddOpenApi/MapOpenApi came unwired and the project's only endpoint "
            + "documentation is gone");

        string json = await response.Content.ReadAsStringAsync();

        // The reader, used here and nowhere else: well-formed JSON is not the same as a usable spec, and a
        // document transformer can emit structurally invalid OpenAPI without throwing. Every client generator
        // starts by doing exactly this, so a document that fails here is unusable however good it looks.
        ReadResult parsed = OpenApiModelFactory.Parse(json, "json");

        parsed.Diagnostic.Should().NotBeNull("the reader must report on what it read");
        parsed.Diagnostic!.Errors.Should().BeEmpty(
            "a document that does not parse cleanly cannot be consumed by Scalar or by any client generator");
        parsed.Document.Should().NotBeNull();

        new[] { OpenApiSpecVersion.OpenApi3_0, OpenApiSpecVersion.OpenApi3_1 }
            .Should().Contain(parsed.Diagnostic.SpecificationVersion,
                "gh#604 specifies an OpenAPI 3 document; .NET 10's generator emits 3.1.1 today, and either 3.0 or "
                + "3.1 satisfies the requirement — a 2.0 (Swagger) document would not");

        // Cheap, and it pins that the document transformer ran at all: without it the title defaults to the
        // assembly name, and Scalar would present the API under a name nobody chose.
        parsed.Document!.Info.Title.Should().Be("Trading Co-Pilot API");
    }

    // =================================================================================================================
    // Nothing has silently dropped out of it
    // =================================================================================================================

    [Fact]
    public async Task TheSpec_ShouldDescribeEveryEndpointGroup()
    {
        using JsonDocument spec = await FetchSpecAsync();

        string[] paths = [.. spec.RootElement.GetProperty("paths").EnumerateObject().Select(path => path.Name)];

        // Guard the guard. Every assertion below is a containment check, and containment checks over a document
        // that turned out to be nearly empty pass by being asked almost nothing. The application maps 59
        // operations today; a document offering fewer than 55 has lost an area, whether or not it lost an anchor.
        paths.Should().NotBeEmpty("a document with no paths describes nothing");
        Operations(spec).Should().HaveCountGreaterThanOrEqualTo(55,
            "the host maps 59 operations into the document; materially fewer means a group stopped being "
            + "described and the anchor checks below may be passing over a shrunken document");

        foreach (string anchor in AnchorPaths.Keys)
        {
            paths.Should().Contain(anchor,
                $"{anchor} anchors an endpoint group Program.cs maps with a single line — its absence means that "
                + "line was dropped or moved after MapTradingCopilotApiReference, and every route in the group "
                + "has vanished from the spec with nothing else to say so");
        }
    }

    [Fact]
    public async Task EveryOperation_ShouldBeTagged_SoScalarGroupsThem()
    {
        using JsonDocument spec = await FetchSpecAsync();

        List<string> untagged = [];
        List<string> defaulted = [];
        foreach (SpecOperation operation in Operations(spec))
        {
            string[] tags = TagsOf(operation);
            if (tags.Length == 0)
            {
                untagged.Add(operation.Key);
            }
            else if (tags.Contains(ProbeDefaultTag, StringComparer.Ordinal))
            {
                defaulted.Add(operation.Key);
            }
        }

        untagged.Should().BeEmpty("Scalar renders the reference UI grouped by tag; an untagged operation has no "
            + "heading to appear under");

        // The sharp one — see GroupTags for why an exact set rather than a presence check. A group that loses its
        // .WithTags(...) does not become untagged; it acquires a NEW tag named after its endpoint class, and only
        // pinning the whole set can notice that.
        string[] present = [.. Operations(spec).SelectMany(TagsOf).Distinct(StringComparer.Ordinal)];
        present.Should().BeEquivalentTo([.. GroupTags, ProbeDefaultTag],
            "these are the headings the Scalar reference UI presents (gh#604). A name here that is not in "
            + "GroupTags is an endpoint module that lost its .WithTags(...) and is now filed under its own class "
            + "name; a name missing from it is a group that was renamed, retagged, or unmapped");

        defaulted.Should().BeEquivalentTo(UntaggedByOmission,
            $"only the two infrastructure probes may fall into the '{ProbeDefaultTag}' bucket — an API operation "
            + "arriving there was mapped inline in Program.cs instead of in a tagged endpoint module");
    }

    [Fact]
    public async Task EachEndpointGroup_ShouldCarryItsOwnTag()
    {
        using JsonDocument spec = await FetchSpecAsync();

        JsonElement paths = spec.RootElement.GetProperty("paths");

        foreach ((string anchor, string expectedTag) in AnchorPaths)
        {
            paths.TryGetProperty(anchor, out JsonElement pathItem).Should().BeTrue(
                $"{anchor} must be described before its tag can be checked");

            foreach (SpecOperation operation in OperationsOf(anchor, pathItem))
            {
                TagsOf(operation).Should().Contain(expectedTag,
                    $"gh#604 tags each group so Scalar shows it under its own heading; {operation.Key} losing "
                    + $"'{expectedTag}' means the group's MapGroup(...).WithTags(...) changed and its section of "
                    + "the reference UI moved or disappeared");
            }
        }
    }

    // =================================================================================================================
    // The spec's security tells the truth about the router
    // =================================================================================================================

    [Fact]
    public async Task TheSpec_ShouldDeclareTheBearerScheme()
    {
        using JsonDocument spec = await FetchSpecAsync();

        spec.RootElement.TryGetProperty("components", out JsonElement components).Should().BeTrue(
            "the document must carry a components object to declare its security scheme in");
        components.TryGetProperty("securitySchemes", out JsonElement schemes).Should().BeTrue();
        schemes.TryGetProperty("Bearer", out JsonElement bearer).Should().BeTrue(
            "BearerSecurity.SchemeName is 'Bearer'; the whole document's security refers to it by that name, so a "
            + "rename breaks every operation's requirement at once");

        bearer.GetProperty("type").GetString().Should().Be("http");
        bearer.GetProperty("scheme").GetString().Should().Be("bearer");
        bearer.GetProperty("bearerFormat").GetString().Should().Be("JWT",
            "the deployment authenticates with a JWT from POST /auth/login (R-18) — a client generator reads "
            + "these three fields to decide how to send the credential, and gets it wrong if they drift");
    }

    [Fact]
    public async Task SecuredOperations_ShouldDocumentASecurityRequirement_AndAnonymousOnesShouldNot()
    {
        using JsonDocument spec = await FetchSpecAsync();

        IReadOnlyList<SpecOperation> operations = Operations(spec);

        // Guard the guard: both halves of the derivation must actually be present to be checked, and the named
        // open set must be real rather than four strings matching nothing.
        string[] keys = [.. operations.Select(operation => operation.Key)];
        keys.Should().Contain("GET /auth/me", "the secured half of gh#612's criterion must exist to be asserted");
        keys.Should().Contain("POST /auth/login", "so must the anonymous half");
        foreach (string open in OpenOperations)
        {
            keys.Should().Contain(open, $"{open} is listed as documented-open, so it must be in the document");
        }

        List<string> missingRequirement = [];
        List<string> unexpectedRequirement = [];
        foreach (SpecOperation operation in operations)
        {
            bool declared = operation.Node.TryGetProperty("security", out JsonElement security)
                && security.ValueKind == JsonValueKind.Array
                && security.GetArrayLength() > 0;

            if (OpenOperations.Contains(operation.Key, StringComparer.Ordinal))
            {
                if (declared)
                {
                    unexpectedRequirement.Add(operation.Key);
                }
            }
            else if (!declared)
            {
                missingRequirement.Add(operation.Key);
            }
        }

        // This is BearerSecurity.RequiresBearer end to end: the transformer reads each endpoint's own
        // authorization metadata, so an operation appearing here is not a documentation slip — the ROUTE lost its
        // .RequireAuthorization(), and R-18's sweep and this one go red together.
        missingRequirement.Should().BeEmpty(
            "every route except the two auth entrypoints and the two probes sits behind the gate (R-18), so the "
            + "spec must say so — an operation documented as open either lost its .RequireAuthorization() or "
            + "gained an [AllowAnonymous] without being put on the record");

        unexpectedRequirement.Should().BeEmpty(
            "AllowAnonymous wins over an inherited group requirement, and the spec must match how the route "
            + "actually behaves — telling a caller that POST /auth/login needs a token makes sign-in undocumented");
    }

    [Fact]
    public async Task TheBearerRequirement_ShouldNameTheBearerScheme()
    {
        using JsonDocument spec = await FetchSpecAsync();

        JsonElement requirement = spec.RootElement
            .GetProperty("paths").GetProperty("/auth/me").GetProperty("get")
            .GetProperty("security")[0];

        // ------------------------------------------------------------------------------------------------------
        // DEFECT gh#680 (in gh#604's BearerSecurityRequirementTransformer; found by gh#612) — PINNING OBSERVED
        // BEHAVIOUR, NOT BLESSING IT. Per the QA contract §"Pin an observed defect; never bless it".
        //
        // Every secured operation emits `"security": [ { } ]` — an EMPTY requirement object. The scheme name is
        // absent, so nothing in the document ever references the `Bearer` scheme declared in components. In
        // OpenAPI an empty security requirement object means "no security required", so the shipped spec tells
        // Scalar and every client generator that POST /accounts/{id}/orders and POST /kill-switch are open — the
        // exact inversion of what gh#604 set out to express.
        //
        // Cause, reproduced in isolation against Microsoft.OpenApi 2.11.0: the requirement's key is built as
        //     new OpenApiSecuritySchemeReference(BearerSecurity.SchemeName, hostDocument: null)
        // and a reference with no host document cannot resolve its target, so the serializer writes no key.
        // Passing the document instead — `context.Document`, which OpenApiOperationTransformerContext exposes —
        // emits `{ "Bearer": [] }`. It is a one-argument fix, and it belongs to the Coding Agent: QA contract §3,
        // "a suite that cannot go green without a production change has found a defect — report it, don't fix it".
        //
        // WHEN THE FIX LANDS this test goes red, and that red is its purpose: replace the assertion below with
        //     requirement.TryGetProperty("Bearer", out JsonElement scopes).Should().BeTrue(...);
        //     scopes.GetArrayLength().Should().Be(0, "an http/bearer scheme takes no scopes");
        // and it becomes the fix's regression guard. Do not simply delete it.
        //
        // Note what this does NOT undermine: SecuredOperations_ShouldDocumentASecurityRequirement_AndAnonymousOnesShouldNot
        // above still holds and is still meaningful — the PRESENCE of the (empty) entry tracks the endpoint's
        // authorization metadata correctly, so RequiresBearer itself is demonstrably right end to end. What is
        // broken is only the requirement's contents. Neither test may be cited as evidence that the shipped spec
        // advertises authentication, because it does not.
        // ------------------------------------------------------------------------------------------------------
        requirement.EnumerateObject().Should().BeEmpty(
            "DEFECT gh#680: the Bearer requirement serializes as an empty object because the scheme reference is "
            + "built with hostDocument: null — see the comment above; this assertion pins reality until the fix lands");
    }

    // =================================================================================================================
    // The interactive console, and the environment gate on it
    // =================================================================================================================

    [Fact]
    public async Task TheScalarConsole_ShouldBeServed_OutsideProduction()
    {
        // Read the route TABLE, not just the status code. /scalar/v1 has no file extension, so an unmapped
        // console falls through to the SPA fallback (MapFallbackToFile, ADR-0020) — which 404s only because this
        // host has no built bundle. A status-code-only check would therefore pass for the wrong reason the moment
        // a bundle is present, and would be green against a host that maps no console at all. The route's
        // existence is a property of the host, so assert that.
        string[] scalarRoutes =
        [
            .. MappedRoutes(_factory.Services).Where(route => route.StartsWith("/scalar", StringComparison.Ordinal))
        ];

        scalarRoutes.Should().Contain("/scalar/{documentName?}",
            "ScalarUiPolicy enables the interactive reference UI everywhere except Production (gh#604), and the "
            + "test host is Development — the console being absent here means the policy or its wiring inverted");

        using HttpResponseMessage response = await _client.GetAsync("/scalar/v1");
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the mapped console must actually render — it is the browsable surface #605 pointed the README at");
    }

    // =================================================================================================================
    // Helpers
    // =================================================================================================================

    /// <summary>Fetches the document, failing loudly rather than throwing an opaque parse error on a 404 body.</summary>
    private async Task<JsonDocument> FetchSpecAsync()
    {
        using HttpResponseMessage response = await _client.GetAsync(SpecPath);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{SpecPath} must be served for anything to assert on");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Every (verb, path) operation in the document.</summary>
    private static IReadOnlyList<SpecOperation> Operations(JsonDocument spec) =>
    [
        .. spec.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => OperationsOf(path.Name, path.Value))
    ];

    /// <summary>
    /// The operations of one path item. A path item may also carry <c>summary</c>, <c>parameters</c> and friends,
    /// which are not operations — filtering by verb keeps those from being probed for tags and security they
    /// could never have.
    /// </summary>
    private static IEnumerable<SpecOperation> OperationsOf(string path, JsonElement pathItem) =>
        pathItem.EnumerateObject()
            .Where(member => HttpVerbs.Contains(member.Name))
            .Select(member => new SpecOperation(member.Name.ToUpperInvariant(), path, member.Value));

    private static HashSet<string> HttpVerbs { get; } =
        new(["get", "put", "post", "delete", "options", "head", "patch", "trace"], StringComparer.OrdinalIgnoreCase);

    private static string[] TagsOf(SpecOperation operation) =>
        operation.Node.TryGetProperty("tags", out JsonElement tags) && tags.ValueKind == JsonValueKind.Array
            ? [.. tags.EnumerateArray().Select(tag => tag.GetString() ?? string.Empty)]
            : [];

    /// <summary>
    /// The host's real route templates. <c>IEnumerable&lt;EndpointDataSource&gt;</c> rather than a single
    /// resolve, for the reason <see cref="AuthorizationSurfaceIntegrationTests"/> records: the host composes
    /// several sources and resolving the one service returns only one of them.
    /// </summary>
    internal static IReadOnlyList<string> MappedRoutes(IServiceProvider services) =>
    [
        .. services.GetRequiredService<IEnumerable<EndpointDataSource>>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(template => template is not null)
            .Select(template => "/" + template!.Trim('/'))
            .Distinct(StringComparer.Ordinal)
    ];

    /// <summary>One operation in the document: its verb, its path, and the JSON object describing it.</summary>
    private sealed record SpecOperation(string Method, string Path, JsonElement Node)
    {
        /// <summary>The stable identity used in allow-lists and failure messages, e.g. <c>POST /kill-switch</c>.</summary>
        public string Key => $"{Method} {Path}";
    }
}

/// <summary>
/// The production half of gh#604's Scalar gate: in a host running as <c>Production</c> the interactive console
/// must not be mapped at all, while the generated spec must still be served (gh#612).
/// </summary>
/// <remarks>
/// <para>
/// Split into its own class because it needs a differently-configured host —
/// <see cref="ProductionDocsPostgresFactory"/>, which records why the second container is worth its cost. The
/// non-production half lives in
/// <see cref="OpenApiDocumentIntegrationTests.TheScalarConsole_ShouldBeServed_OutsideProduction"/>; neither half
/// means much without the other, since a gate that is always open and a gate that is always shut each satisfy
/// exactly one of them.
/// </para>
/// <para>
/// This is the HTTP-surface complement to <c>ScalarUiPolicyTests</c> (gh#604), not a repeat of it: that suite
/// proves the policy <i>function</i> answers correctly, this one proves the composition root <i>asks</i> it and
/// acts on the answer.
/// </para>
/// </remarks>
public class ScalarProductionGateIntegrationTests : IClassFixture<ProductionDocsPostgresFactory>
{
    private readonly ProductionDocsPostgresFactory _factory;
    private readonly HttpClient _client;

    public ScalarProductionGateIntegrationTests(ProductionDocsPostgresFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public void TheScalarConsole_ShouldNotBeMapped_InProduction()
    {
        IReadOnlyList<string> routes = OpenApiDocumentIntegrationTests.MappedRoutes(_factory.Services);

        // Vacuity guard, and only that: "no /scalar route" over an empty or half-built route table is not
        // evidence of a gate. Deliberately slack against the 47 templates mapped today — tightening it would
        // turn an unrelated dropped endpoint group into a confusing red HERE, where the subject is the console.
        routes.Should().HaveCountGreaterThanOrEqualTo(40, "the production host maps the same API as any other");

        routes.Where(route => route.StartsWith("/scalar", StringComparison.Ordinal)).Should().BeEmpty(
            "in production the interactive console would put a one-click trigger for POST /accounts/{id}/orders "
            + "and POST /kill-switch — against the LIVE, real-money venue — in front of whoever reaches the page "
            + "(gh#604). Asserted on the route table rather than a 404 because /scalar/v1 falls through to the "
            + "SPA fallback (ADR-0020), which would answer 200 with index.html wherever a bundle is built");
    }

    [Fact]
    public async Task TheGeneratedSpec_ShouldStillBeServed_InProduction()
    {
        // The other half of the gate's honesty, and the distinction gh#604 turns on: only the interactive console
        // is environment-gated. The JSON describes shape and nothing else — no data, no action, every write still
        // behind a JWT — so it ships everywhere, and it is what the README links to. A gate that took the whole
        // documentation surface down in production would satisfy the test above and be wrong.
        using HttpResponseMessage response = await _client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the OpenAPI document is served in every environment (gh#604); only the browsable console is gated");
    }
}
