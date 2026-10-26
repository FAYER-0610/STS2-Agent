using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace STS2AIAgent.Agent;

/// <summary>
/// Local patch: lets a streaming narrator speak a move before autoplay executes it.
/// </summary>
/// <remarks>
/// Off unless <c>%APPDATA%\STS2AIAgent\narrator.txt</c> holds a loopback URL. When on, every
/// validated act is POSTed there first, and the narrator answers once it has said its line (or
/// at once when the move is not worth a line). The narrator only ever delays a move; it never
/// changes or vetoes it. A missing, slow or broken narrator costs at most <see cref="MaxWait"/>
/// once, then the gate stays open for <see cref="RetryAfter"/> so a closed narrator does not
/// slow every card.
/// </remarks>
internal static class NarrationGate
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConfigTtl = TimeSpan.FromSeconds(10);

    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly object Gate = new();
    private static Uri? _endpoint;
    private static DateTime _readAt = DateTime.MinValue;
    private static DateTime _openUntil = DateTime.MinValue;

    public static async Task BeforeActAsync(
        string action,
        int? cardIndex,
        int? targetIndex,
        int? optionIndex,
        string? reason,
        CancellationToken cancellationToken)
    {
        var endpoint = Endpoint();
        if (endpoint == null)
        {
            return;
        }

        lock (Gate)
        {
            if (DateTime.UtcNow < _openUntil)
            {
                return;
            }
        }

        var body = JsonSerializer.Serialize(new
        {
            action,
            card_index = cardIndex,
            target_index = targetIndex,
            option_index = optionIndex,
            reason = reason is { Length: > 600 } ? reason[..600] : reason
        });
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(MaxWait);
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(endpoint, content, wait.Token);
            if (response.IsSuccessStatusCode)
            {
                return;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (HttpRequestException)
        {
        }

        lock (Gate)
        {
            _openUntil = DateTime.UtcNow + RetryAfter;
        }
    }

    private static readonly TimeSpan GuidanceWait = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Local patch: the narrator's board of recent viewer suggestions (<c>GET /api/guidance</c> next to
    /// the narrate URL). Read-only advice for the model; null when the narrator is off or silent.
    /// </summary>
    public static async Task<ViewerGuidance?> ReadGuidanceAsync(CancellationToken cancellationToken)
    {
        var endpoint = Endpoint();
        if (endpoint == null)
        {
            return null;
        }

        lock (Gate)
        {
            if (DateTime.UtcNow < _openUntil)
            {
                return null;
            }
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(GuidanceWait);
        try
        {
            using var response = await Http.GetAsync(new Uri(endpoint, "/api/guidance"), wait.Token);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(wait.Token));
                return ViewerGuidance.Parse(json.RootElement);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (HttpRequestException)
        {
        }
        catch (JsonException)
        {
        }

        lock (Gate)
        {
            _openUntil = DateTime.UtcNow + RetryAfter;
        }

        return null;
    }

    private static Uri? Endpoint()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _readAt < ConfigTtl)
            {
                return _endpoint;
            }

            _readAt = DateTime.UtcNow;
            _endpoint = null;
            try
            {
                var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var path = Path.Combine(root, "STS2AIAgent", "narrator.txt");
                if (!string.IsNullOrWhiteSpace(root) && File.Exists(path)
                    && Uri.TryCreate(File.ReadAllText(path).Trim(), UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
                {
                    _endpoint = uri;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return _endpoint;
        }
    }
}

/// <summary>Local patch: recent viewer suggestions, newest last. Advice only, never an action.</summary>
internal sealed record ViewerGuidance(long Version, IReadOnlyList<(string Name, string Text, int Age)> Items)
{
    private const int MaxItems = 5;

    public static ViewerGuidance? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var v) || !v.TryGetInt64(out var version))
        {
            return null;
        }

        var items = new List<(string, string, int)>();
        if (root.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray().Take(MaxItems))
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    && item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    var age = item.TryGetProperty("age", out var a) && a.TryGetInt32(out var seconds) ? seconds : 0;
                    items.Add((Clip(n.GetString(), 24), Clip(t.GetString(), 80), age));
                }
            }
        }

        return new ViewerGuidance(version, items);
    }

    /// <summary>The suggestions as prompt lines, oldest first; only the last <paramref name="newest"/> when given.</summary>
    public string Lines(int newest = MaxItems) => string.Join("\n", Items
        .Skip(Math.Max(0, Items.Count - newest))
        .Select(item => $"- {item.Name} ({item.Age}s ago): {item.Text}"));

    private static string Clip(string? text, int max)
    {
        var flat = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length > max ? flat[..max] : flat;
    }
}
