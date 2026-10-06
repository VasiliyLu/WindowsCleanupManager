using System.Security.Cryptography;
using System.Text;
using Wcm.Scanning;
using Wcm.Storage;

namespace Wcm.Classification;

public sealed class CachedDecision
{
    public string Fingerprint { get; set; } = "";
    public double Deletable { get; set; }
    public string Category { get; set; } = "";
    public double CategoryConfidence { get; set; }
    public DateTime At { get; set; }
}

/// <summary>Remembers Jev answers per path, so repeated scans don't pay for the same question.</summary>
public sealed class DecisionCache
{
    private readonly Dictionary<string, CachedDecision> _entries;
    private readonly string? _path;
    private readonly TimeSpan _ttl;
    private readonly Lock _lock = new();
    private bool _dirty;

    public DecisionCache(string? path, int ttlDays)
    {
        _path = path;
        _ttl = TimeSpan.FromDays(ttlDays);
        Dictionary<string, CachedDecision>? loaded = null;
        try { if (path is not null) loaded = Json.Load<Dictionary<string, CachedDecision>>(path); }
        catch (Exception) { /* corrupt cache: start over */ }
        _entries = new Dictionary<string, CachedDecision>(loaded ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public int Count => _entries.Count;

    /// <summary>
    /// Changes when the set of biggest children changes or size roughly doubles/halves.
    /// Deliberately ignores mtime: caches are touched constantly but stay caches.
    /// </summary>
    public static string Fingerprint(FsNode node, long effectiveSize)
    {
        var sb = new StringBuilder();
        sb.Append(node.IsDir ? 'd' : 'f').Append('|');
        sb.Append(effectiveSize <= 0 ? 0 : (int)Math.Log2(effectiveSize)).Append('|');
        foreach (var name in node.Children.OrderByDescending(c => c.Size).Take(20).Select(c => c.Name.ToLowerInvariant()).Order(StringComparer.Ordinal))
            sb.Append(name).Append('/');
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    public CachedDecision? Get(string path, string fingerprint)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(path, out var e)) return null;
            if (e.Fingerprint != fingerprint || DateTime.UtcNow - e.At > _ttl) return null;
            return e;
        }
    }

    public void Put(string path, string fingerprint, JevVerdict v)
    {
        lock (_lock)
        {
            _entries[path] = new CachedDecision
            {
                Fingerprint = fingerprint,
                Deletable = v.Deletable,
                Category = v.Category,
                CategoryConfidence = v.CategoryConfidence,
                At = DateTime.UtcNow
            };
            _dirty = true;
        }
    }

    public void Save()
    {
        if (_path is null) return;
        lock (_lock)
        {
            if (!_dirty) return;
            var now = DateTime.UtcNow;
            foreach (var key in _entries.Where(kv => now - kv.Value.At > _ttl).Select(kv => kv.Key).ToList())
                _entries.Remove(key);
            Json.Save(_path, _entries);
            _dirty = false;
        }
    }
}
