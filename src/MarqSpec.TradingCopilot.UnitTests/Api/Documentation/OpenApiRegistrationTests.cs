using MarqSpec.TradingCopilot.Api.Documentation;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Documentation;

/// <summary>
/// The composition-root wiring of the generated API document (gh#604). What this guards is that
/// <see cref="OpenApiRegistration.AddTradingCopilotOpenApi"/> actually stands the OpenAPI document generator up —
/// a refactor that dropped the <c>AddOpenApi</c> call would leave <c>/openapi/v1.json</c> returning 404 with every
/// other test still green. (The document's <i>content</i> — paths, tags, the Bearer scheme — is the QA integration
/// tier's job, because it needs the real host pipeline; QA(task#604).)
/// </summary>
public class OpenApiRegistrationTests
{
    [Fact]
    public void AddTradingCopilotOpenApi_ShouldRegisterTheOpenApiDocumentGenerator()
    {
        ServiceCollection services = new();

        // Vacuity guard: nothing configures OpenApiOptions until the call under test does.
        services.Any(descriptor => descriptor.ServiceType == typeof(IConfigureOptions<OpenApiOptions>))
            .Should().BeFalse();

        services.AddTradingCopilotOpenApi();

        services.Any(descriptor => descriptor.ServiceType == typeof(IConfigureOptions<OpenApiOptions>))
            .Should().BeTrue("AddOpenApi must configure the document — without it /openapi/v1.json would 404 (gh#604)");
    }
}
