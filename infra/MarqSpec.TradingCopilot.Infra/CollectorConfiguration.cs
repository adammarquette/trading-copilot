using System.Reflection;

namespace MarqSpec.TradingCopilot.Infra;

/// <summary>
/// The checked-in OTLP collector configuration, embedded and read at synth time so
/// <c>cdk synth</c> does not depend on the working directory.
/// </summary>
public static class CollectorConfiguration
{
    private const string ResourceName = "otel-collector-config.yaml";

    /// <summary>The file's text, exactly as it is checked in.</summary>
    public static string Yaml { get; } = Read();

    private static string Read()
    {
        using var stream = typeof(CollectorConfiguration).GetTypeInfo().Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{ResourceName}' is missing from the Infra assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
