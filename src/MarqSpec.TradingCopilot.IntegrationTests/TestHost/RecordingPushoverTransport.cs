using System.Globalization;
using System.Net;
using System.Text.Json;

namespace MarqSpec.TradingCopilot.IntegrationTests.TestHost;

/// <summary>
/// A fake <c>api.pushover.net</c> (gh#442) — the <b>wire</b>, and the only thing a notification harness substitutes.
/// Everything above it (the outbox, the relay, the queue, the dedup decorator, and the real
/// <c>PushoverNotificationChannel</c> with its severity→priority mapping) stays exactly what production composed, so
/// a future change to the chain is inherited rather than silently diverged from.
/// </summary>
/// <remarks>
/// It records the decoded form posts a page would really have made, and can fail the way a transport fails — reject
/// with a status, throw a socket fault, or hang — so the fail-safe paths are proven <b>through</b> production's own
/// error handling rather than around it.
/// </remarks>
public sealed class RecordingPushoverTransport
{
    private readonly List<PushoverPost> _sent = [];
    private readonly List<string> _cancelled = [];
    private readonly Lock _gate = new();
    private HttpStatusCode? _rejectWith;
    private bool _throws;
    private TimeSpan _delay = TimeSpan.Zero;
    private int _receiptCounter;

    /// <summary>One decoded POST to <c>/1/messages.json</c>.</summary>
    /// <param name="Title">The notification title.</param>
    /// <param name="Message">The notification body.</param>
    /// <param name="Priority">Pushover priority; <c>"2"</c> is Emergency — the actual pager.</param>
    /// <param name="Retry">The Emergency retry interval, when sent.</param>
    /// <param name="Expire">The Emergency expiry, when sent.</param>
    public sealed record PushoverPost(string Title, string Message, string Priority, string? Retry, string? Expire);

    /// <summary>Every message post that reached the wire, in order.</summary>
    public IReadOnlyList<PushoverPost> Sent
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    /// <summary>Only the Emergency-priority posts — the ones that actually page an on-call-of-one.</summary>
    public IReadOnlyList<PushoverPost> Pages =>
        [.. Sent.Where(post => post.Priority == "2")];

    /// <summary>Receipts cancelled via <c>/1/receipts/{id}/cancel.json</c> — a page withdrawn after the fact.</summary>
    public IReadOnlyList<string> Cancelled
    {
        get
        {
            lock (_gate)
            {
                return [.. _cancelled];
            }
        }
    }

    /// <summary>Makes Pushover answer with <paramref name="status"/> — an unaccepted page is still owed.</summary>
    public void MakeSendReject(HttpStatusCode status)
    {
        lock (_gate)
        {
            _rejectWith = status;
        }
    }

    /// <summary>Makes the socket fault — the real client swallows this and returns false.</summary>
    public void MakeSendThrow()
    {
        lock (_gate)
        {
            _throws = true;
        }
    }

    /// <summary>Makes the wire hang, for the R-13 "a dead channel must not delay the flatten" guard.</summary>
    public void MakeSendHang(TimeSpan delay)
    {
        lock (_gate)
        {
            _delay = delay;
        }
    }

    /// <summary>Clears recorded traffic and restores a healthy wire — call per test.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _sent.Clear();
            _cancelled.Clear();
            _rejectWith = null;
            _throws = false;
            _delay = TimeSpan.Zero;
            _receiptCounter = 0;
        }
    }

    internal async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        TimeSpan delay;
        bool throws;
        HttpStatusCode? reject;
        lock (_gate)
        {
            delay = _delay;
            throws = _throws;
            reject = _rejectWith;
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }

        if (throws)
        {
            throw new HttpRequestException("the pushover wire is down (test)");
        }

        string path = request.RequestUri?.AbsolutePath ?? string.Empty;

        // A receipt cancel: /1/receipts/{id}/cancel.json
        if (path.Contains("/receipts/", StringComparison.Ordinal))
        {
            string receipt = path.Split('/').SkipWhile(part => part != "receipts").Skip(1).FirstOrDefault() ?? "?";
            lock (_gate)
            {
                _cancelled.Add(receipt);
            }

            return Json("""{"status":1}""");
        }

        string body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Dictionary<string, string> form = Decode(body);

        if (reject is { } status)
        {
            return new HttpResponseMessage(status) { Content = new StringContent("""{"status":0}""") };
        }

        string receiptId;
        lock (_gate)
        {
            _sent.Add(new PushoverPost(
                form.GetValueOrDefault("title", string.Empty),
                form.GetValueOrDefault("message", string.Empty),
                form.GetValueOrDefault("priority", string.Empty),
                form.GetValueOrDefault("retry"),
                form.GetValueOrDefault("expire")));
            receiptId = $"receipt-{++_receiptCounter}";
        }

        // Emergency posts get a receipt back, so the channel has something to cancel on resolve.
        return form.GetValueOrDefault("priority") == "2"
            ? Json($$"""{"status":1,"receipt":"{{receiptId}}"}""")
            : Json("""{"status":1}""");
    }

    private static HttpResponseMessage Json(string payload) =>
        new(HttpStatusCode.OK) { Content = new StringContent(payload) };

    private static Dictionary<string, string> Decode(string form)
    {
        Dictionary<string, string> decoded = new(StringComparer.Ordinal);
        foreach (string pair in form.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            decoded[Uri.UnescapeDataString(parts[0])] =
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
        }

        return decoded;
    }

    /// <summary>Formats a count for a failure message without culture surprises.</summary>
    internal static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The primary handler swapped under production's typed <c>PushoverNotificationChannel</c> client (gh#442).</summary>
internal sealed class FakePushoverHandler(RecordingPushoverTransport transport) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        transport.HandleAsync(request, cancellationToken);
}
