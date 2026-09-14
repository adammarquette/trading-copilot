using System.Text.Json;
using MarqSpec.TradingCopilot.Domain.Risk;

namespace MarqSpec.TradingCopilot.Data;

/// <summary>
/// Serialises the advisories a gate decision raised, for the <c>jsonb</c> column on <c>GateDecisions</c> (gh#407).
/// </summary>
/// <remarks>
/// <para>
/// Stored as JSON rather than a child table because an advisory is a <b>detail of one decision</b>, read back with
/// that decision and never queried on its own. It follows the same shape the event log already uses for
/// <c>Event.Payload</c>. If advisories ever need aggregating — "how often was this operator warned before a
/// breach?" — that is the point to promote them to their own table, not before.
/// </para>
/// <para>
/// <b>Empty serialises to <see langword="null"/>, not <c>"[]"</c>.</b> A row is written on every sized attempt and
/// the overwhelming majority raise nothing, so storing an empty array would be noise on most rows. Null also means
/// the same thing as a row written before this column existed, so old and new rows read alike.
/// </para>
/// </remarks>
public static class GateAdvisoryJson
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    /// <summary>Serialises the advisories, or <see langword="null"/> when there are none.</summary>
    /// <param name="advisories">The advisories the decision carried.</param>
    /// <returns>The JSON, or <see langword="null"/> when the list is empty.</returns>
    public static string? Serialize(IReadOnlyList<GateAdvisory> advisories)
    {
        ArgumentNullException.ThrowIfNull(advisories);
        return advisories.Count == 0 ? null : JsonSerializer.Serialize(advisories, _options);
    }

    /// <summary>Reads advisories back, treating absent or unreadable JSON as none.</summary>
    /// <param name="json">The stored JSON.</param>
    /// <returns>The advisories; empty when the column is null.</returns>
    public static IReadOnlyList<GateAdvisory> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<GateAdvisory>>(json, _options) ?? [];
    }
}
