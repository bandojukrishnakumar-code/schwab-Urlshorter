using System.Text.Json;
using System.Text.RegularExpressions;

namespace UrlShortener;

public sealed record ShortUrl(
    string Code,
    string Url,
    DateTimeOffset CreatedAt,
    long Clicks,
    DateTimeOffset? LastAccessedAt,
    IReadOnlyDictionary<string, long> Referrers);

public sealed class UrlStoreException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class UrlStore(string filePath)
{
    private static readonly Regex CodePattern = new(
        "^[A-Za-z0-9_-]{4,32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, MutableShortUrl> records = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        if (!File.Exists(filePath))
        {
            return;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var stream = File.OpenRead(filePath);
            var storedRecords = await JsonSerializer.DeserializeAsync<List<MutableShortUrl>>(
                stream, jsonOptions, cancellationToken) ?? [];
            records.Clear();
            foreach (var record in storedRecords)
            {
                if (!CodePattern.IsMatch(record.Code) || !records.TryAdd(record.Code, record))
                {
                    throw new InvalidDataException("The URL data file contains an invalid or duplicate short code.");
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ShortUrl> CreateAsync(string url, string? alias, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var code = alias ?? Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            if (!CodePattern.IsMatch(code))
            {
                throw new UrlStoreException(400, "Alias must be 4-32 letters, numbers, hyphens, or underscores.");
            }
            if (records.ContainsKey(code))
            {
                throw new UrlStoreException(409, "That short code is already in use.");
            }

            var record = new MutableShortUrl
            {
                Code = code,
                Url = url,
                CreatedAt = DateTimeOffset.UtcNow,
                Referrers = new(StringComparer.OrdinalIgnoreCase)
            };
            records.Add(code, record);
            try
            {
                await PersistAsync(cancellationToken);
            }
            catch
            {
                records.Remove(code);
                throw;
            }
            return ToSnapshot(record);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string?> ResolveAsync(
        string code,
        string referrer,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!records.TryGetValue(code, out var record))
            {
                return null;
            }

            record.Clicks++;
            record.LastAccessedAt = DateTimeOffset.UtcNow;
            var safeReferrer = referrer.Length > 253 ? referrer[..253] : referrer;
            record.Referrers[safeReferrer] = record.Referrers.GetValueOrDefault(safeReferrer) + 1;
            await PersistAsync(cancellationToken);
            return record.Url;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ShortUrl?> GetAsync(string code, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return records.TryGetValue(code, out var record) ? ToSnapshot(record) : null;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShortUrl>> ListAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return records.Values.Select(ToSnapshot).ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public static string ValidateDestination(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(parsed.Host))
        {
            throw new UrlStoreException(400, "URL must be an absolute HTTP or HTTPS address no longer than 2048 characters.");
        }
        return parsed.ToString();
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(filePath);
        var temporaryPath = $"{fullPath}.tmp";
        await using (var stream = new FileStream(
            temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, records.Values, jsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temporaryPath, fullPath, overwrite: true);
    }

    private static ShortUrl ToSnapshot(MutableShortUrl record) =>
        new(record.Code, record.Url, record.CreatedAt, record.Clicks,
            record.LastAccessedAt, new Dictionary<string, long>(record.Referrers));

    private sealed class MutableShortUrl
    {
        public string Code { get; set; } = "";
        public string Url { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public long Clicks { get; set; }
        public DateTimeOffset? LastAccessedAt { get; set; }
        public Dictionary<string, long> Referrers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
