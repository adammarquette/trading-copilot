using System.Diagnostics;

namespace MarqSpec.TradingCopilot.Domain.Events;

/// <summary>
/// Carries trace context across the event backbone's <b>async gap</b> (gh#230, ADR-0002, ADR-0001): a producer
/// stamps its W3C <c>traceparent</c> on the envelope, and a consumer <b>span-links</b> to it.
/// </summary>
/// <remarks>
/// <para>
/// A link rather than a parent, deliberately. The consumer is not a child call — it may run minutes later, in
/// another process, and one batch can carry events from many producers. A link expresses "caused by" without
/// pretending the consumer's span lives inside the producer's, which is what lets
/// <c>websocket → append → consume → DB write</c> render as one queryable trace (engineering §7).
/// </para>
/// <para>
/// <b>Every path here degrades rather than throws.</b> The field is optional: pre-migration rows carry none, and a
/// producer outside a trace stamps none. On a log whose consumers ride safety-critical paths, losing a trace is a
/// reporting loss — throwing would stop stop-promotion or conditional firing outright.
/// </para>
/// </remarks>
public static class EventTracing
{
    /// <summary>The active span's W3C <c>traceparent</c>, or <see langword="null"/> outside a trace.</summary>
    /// <returns>The traceparent to stamp on an outgoing event, or <see langword="null"/>.</returns>
    public static string? CurrentTraceParent() => Activity.Current?.Id;

    /// <summary>Builds the link a consumer's span should carry back to the producer that appended the event.</summary>
    /// <param name="traceParent">The envelope's W3C <c>traceparent</c>; may be null, blank, or malformed.</param>
    /// <param name="link">The link to the producer's span, when one could be parsed.</param>
    /// <returns><see langword="true"/> when a link was produced; otherwise <see langword="false"/>.</returns>
    public static bool TryCreateLink(string? traceParent, out ActivityLink link)
    {
        link = default;

        if (string.IsNullOrWhiteSpace(traceParent))
        {
            return false;
        }

        // TryParse handles the version, the hex fields and the flags, and returns false rather than throwing on
        // anything it does not recognise -- including the all-zero trace id, which is valid syntax but names no
        // trace. isRemote: the producer's span belongs to another context by definition.
        if (!ActivityContext.TryParse(traceParent, traceState: null, isRemote: true, out ActivityContext context))
        {
            return false;
        }

        if (context.TraceId == default || context.SpanId == default)
        {
            return false;
        }

        link = new ActivityLink(context);
        return true;
    }
}
