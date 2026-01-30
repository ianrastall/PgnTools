using System.Buffers;
using System.Text;
using ZstdSharp;

namespace PgnTools.Services;

/// <summary>
/// Rating database backed by the embedded Rust-generated ratings.bin.zst file.
/// </summary>
public sealed class EmbeddedRatingsDatabase : IRatingDatabase
{
    private const string RatingsFileName = "ratings.bin.zst";
    private const double FuzzyMinScore = 0.90;
    private const double FuzzyStrongScore = 0.97;
    private const double FuzzyMinGap = 0.02;
    private const double FuzzyLastMinScore = 0.92;
    private const int NameCacheCapacity = 100_000;
    private const int LastCacheCapacity = 10_000;

    private readonly Lazy<Dictionary<string, PlayerRecord>> _db;
    private readonly Lazy<FuzzyIndex> _fuzzyIndex;

    private readonly Dictionary<string, string?> _nameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _lastCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();

    public EmbeddedRatingsDatabase()
    {
        _db = new Lazy<Dictionary<string, PlayerRecord>>(LoadDatabase, LazyThreadSafetyMode.ExecutionAndPublication);
        _fuzzyIndex = new Lazy<FuzzyIndex>(() => new FuzzyIndex(_db.Value), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int? Lookup(string name, int year, int month)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (year < 1 || month is < 1 or > 12)
        {
            return null;
        }

        var db = _db.Value;
        if (db.Count == 0)
        {
            return null;
        }

        var yearU16 = (ushort)Math.Clamp(year, 0, ushort.MaxValue);
        var monthU8 = (byte)month;

        // Direct and swapped-name lookups first.
        var primaryKey = NormalizeKey(name);
        if (primaryKey.Length == 0)
        {
            return null;
        }

        if (TryLookup(db, primaryKey, yearU16, monthU8, out var elo))
        {
            return elo;
        }

        var swapped = SwappedNameKey(name);
        if (!string.IsNullOrWhiteSpace(swapped) &&
            !string.Equals(swapped, primaryKey, StringComparison.Ordinal) &&
            TryLookup(db, swapped, yearU16, monthU8, out elo))
        {
            return elo;
        }

        // Canonicalize and try again.
        var canonical = CanonicalizeName(name);
        if (!string.IsNullOrWhiteSpace(canonical))
        {
            var canonicalKey = NormalizeKey(canonical);
            if (canonicalKey.Length > 0 && TryLookup(db, canonicalKey, yearU16, monthU8, out elo))
            {
                return elo;
            }
        }

        // Fuzzy lookup by last name bucket.
        var matchedKey = GetCachedOrFuzzyMatchKey(db, name, canonical);
        if (matchedKey != null && TryLookup(db, matchedKey, yearU16, monthU8, out elo))
        {
            return elo;
        }

        return null;
    }

    private string? GetCachedOrFuzzyMatchKey(Dictionary<string, PlayerRecord> db, string rawName, string canonical)
    {
        var cacheKey = rawName;

        lock (_cacheLock)
        {
            if (_nameCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var matched = FuzzyMatchKey(db, rawName, canonical);

        lock (_cacheLock)
        {
            if (_nameCache.Count >= NameCacheCapacity)
            {
                _nameCache.Clear();
            }

            _nameCache[cacheKey] = matched;
        }

        return matched;
    }

    private string? FuzzyMatchKey(Dictionary<string, PlayerRecord> db, string rawName, string canonical)
    {
        if (string.IsNullOrWhiteSpace(canonical))
        {
            return null;
        }

        var tokens = canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        var lastKey = LastNameKey(rawName) ?? LastNameKey(canonical);
        if (lastKey == null)
        {
            return null;
        }

        var resolvedLast = ResolveLastName(lastKey);
        if (resolvedLast == null || !_fuzzyIndex.Value.ByLast.TryGetValue(resolvedLast, out var candidates))
        {
            return null;
        }

        if (tokens.Length < 2 && candidates.Count == 1)
        {
            return candidates[0];
        }

        var queryKey = NormalizeKey(canonical);
        if (queryKey.Length == 0)
        {
            return null;
        }

        var swappedQueryKey = SwappedNameKey(canonical);

        var bestScore = 0.0;
        var secondBest = 0.0;
        string? bestKey = null;

        foreach (var candidateKey in candidates)
        {
            if (!db.TryGetValue(candidateKey, out var player))
            {
                continue;
            }

            var candidateCanonical = CanonicalizeName(player.Name);
            if (candidateCanonical.Length == 0)
            {
                continue;
            }

            var candidateKeyNormalized = NormalizeKey(candidateCanonical);
            if (candidateKeyNormalized.Length == 0)
            {
                continue;
            }

            var score = Math.Max(
                JaroWinklerSimilarity(queryKey, candidateKeyNormalized),
                swappedQueryKey is null ? 0.0 : JaroWinklerSimilarity(swappedQueryKey, candidateKeyNormalized));

            if (score > bestScore)
            {
                secondBest = bestScore;
                bestScore = score;
                bestKey = candidateKey;
            }
            else if (score > secondBest)
            {
                secondBest = score;
            }
        }

        if (bestKey == null)
        {
            return null;
        }

        // Apply similar acceptance thresholds to the Rust version.
        if (bestScore >= FuzzyStrongScore)
        {
            return bestKey;
        }

        if (bestScore >= FuzzyMinScore && (bestScore - secondBest) >= FuzzyMinGap)
        {
            return bestKey;
        }

        return null;
    }

    private string? ResolveLastName(string lastKey)
    {
        lock (_cacheLock)
        {
            if (_lastCache.TryGetValue(lastKey, out var cached))
            {
                return cached;
            }
        }

        var resolved = ResolveLastNameCore(lastKey);

        lock (_cacheLock)
        {
            if (_lastCache.Count >= LastCacheCapacity)
            {
                _lastCache.Clear();
            }

            _lastCache[lastKey] = resolved;
        }

        return resolved;
    }

    private string? ResolveLastNameCore(string lastKey)
    {
        var index = _fuzzyIndex.Value;

        if (index.ByLast.ContainsKey(lastKey))
        {
            return lastKey;
        }

        var initial = lastKey[0];
        if (!index.LastByInitial.TryGetValue(initial, out var lastNames))
        {
            return null;
        }

        var bestScore = 0.0;
        string? best = null;
        foreach (var candidate in lastNames)
        {
            var score = JaroWinklerSimilarity(lastKey, candidate);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return bestScore >= FuzzyLastMinScore ? best : null;
    }

    private static bool TryLookup(
        Dictionary<string, PlayerRecord> db,
        string key,
        ushort year,
        byte month,
        out int elo)
    {
        elo = 0;
        if (!db.TryGetValue(key, out var player))
        {
            return false;
        }

        var rating = player.RatingFor(year, month);
        if (!rating.HasValue)
        {
            return false;
        }

        elo = rating.Value;
        return true;
    }

    private static Dictionary<string, PlayerRecord> LoadDatabase()
    {
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "Assets", RatingsFileName);

        if (!File.Exists(path))
        {
            return new Dictionary<string, PlayerRecord>(StringComparer.OrdinalIgnoreCase);
        }

        using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var decompressed = new DecompressionStream(fileStream);

        return LoadFromBincodeStream(decompressed);
    }

    private static Dictionary<string, PlayerRecord> LoadFromBincodeStream(Stream stream)
    {
        // The Rust tool uses bincode default configuration:
        // - little-endian fixed-width integers
        // - sequence/string lengths encoded as u64
        var map = new Dictionary<string, PlayerRecord>(capacity: 600_000, comparer: StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            var record = TryReadPlayerRecord(stream);
            if (record == null)
            {
                break;
            }

            var key = NormalizeKey(record.Name);
            if (key.Length == 0)
            {
                continue;
            }

            record.SortRatings();
            map[key] = record;
        }

        return map;
    }

    private static PlayerRecord? TryReadPlayerRecord(Stream stream)
    {
        // Read the first string length. If we hit EOF here, we're done.
        if (!TryReadUInt64(stream, out var nameLen))
        {
            return null;
        }

        var name = ReadString(stream, nameLen);
        var country = ReadString(stream);
        var fideId = ReadUInt32(stream);
        var bio = ReadString(stream);
        var ratings = ReadRatings(stream);

        return new PlayerRecord(name, country, fideId, bio, ratings);
    }

    private static string ReadString(Stream stream)
    {
        var length = ReadUInt64(stream);
        return ReadString(stream, length);
    }

    private static string ReadString(Stream stream, ulong length)
    {
        if (length == 0)
        {
            return string.Empty;
        }

        if (length > int.MaxValue)
        {
            throw new InvalidDataException("String length exceeds maximum supported size.");
        }

        var byteCount = (int)length;
        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            ReadExactly(stream, buffer.AsSpan(0, byteCount));
            return Encoding.UTF8.GetString(buffer, 0, byteCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static List<PlayerRating> ReadRatings(Stream stream)
    {
        var length = ReadUInt64(stream);
        if (length == 0)
        {
            return new List<PlayerRating>(0);
        }

        if (length > int.MaxValue)
        {
            throw new InvalidDataException("Ratings vector length exceeds maximum supported size.");
        }

        var count = (int)length;
        var list = new List<PlayerRating>(count);
        for (var i = 0; i < count; i++)
        {
            var year = ReadUInt16(stream);
            var month = ReadByte(stream);
            var rating = ReadUInt16(stream);
            list.Add(new PlayerRating(year, month, rating));
        }

        return list;
    }

    private static ushort ReadUInt16(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[2];
        ReadExactly(stream, buffer);
        return (ushort)(buffer[0] | (buffer[1] << 8));
    }

    private static uint ReadUInt32(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        ReadExactly(stream, buffer);
        return (uint)(buffer[0]
                      | (buffer[1] << 8)
                      | (buffer[2] << 16)
                      | (buffer[3] << 24));
    }

    private static ulong ReadUInt64(Stream stream)
    {
        if (!TryReadUInt64(stream, out var value))
        {
            throw new EndOfStreamException("Unexpected end of stream while reading u64.");
        }

        return value;
    }

    private static bool TryReadUInt64(Stream stream, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        var read = stream.Read(buffer);
        if (read == 0)
        {
            value = 0;
            return false;
        }

        if (read < buffer.Length)
        {
            ReadExactly(stream, buffer[read..]);
        }

        value = (ulong)buffer[0]
                | ((ulong)buffer[1] << 8)
                | ((ulong)buffer[2] << 16)
                | ((ulong)buffer[3] << 24)
                | ((ulong)buffer[4] << 32)
                | ((ulong)buffer[5] << 40)
                | ((ulong)buffer[6] << 48)
                | ((ulong)buffer[7] << 56);
        return true;
    }

    private static byte ReadByte(Stream stream)
    {
        var b = stream.ReadByte();
        if (b < 0)
        {
            throw new EndOfStreamException("Unexpected end of stream while reading byte.");
        }

        return (byte)b;
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer[totalRead..]);
            if (read <= 0)
            {
                throw new EndOfStreamException("Unexpected end of stream.");
            }

            totalRead += read;
        }
    }

    private static string NormalizeKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is ' ' or ',')
            {
                continue;
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    private static string? SwappedNameKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var cleaned = name.Replace(",", " ", StringComparison.Ordinal);
        var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var last = parts[^1];
        var reordered = string.Join(' ', new[] { last }.Concat(parts[..^1]));
        var key = NormalizeKey(reordered);
        return key.Length == 0 ? null : key;
    }

    private static string CanonicalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var lower = name.ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        var lastWasSpace = false;

        foreach (var c in lower)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastWasSpace = false;
                continue;
            }

            if (char.IsWhiteSpace(c) || c is ',' or '.' or '-' or '_' or '\'')
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
        }

        return sb.ToString().Trim();
    }

    private static string? LastNameKey(string name)
    {
        var canonical = CanonicalizeName(name);
        if (canonical.Length == 0)
        {
            return null;
        }

        var parts = canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var last = parts[^1];
        return last.Length == 0 ? null : NormalizeKey(last);
    }

    private static double JaroWinklerSimilarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
        {
            return 1.0;
        }

        var jaro = JaroSimilarity(a, b);
        if (jaro <= 0)
        {
            return 0;
        }

        var prefix = 0;
        var maxPrefix = Math.Min(4, Math.Min(a.Length, b.Length));
        while (prefix < maxPrefix && a[prefix] == b[prefix])
        {
            prefix++;
        }

        const double scalingFactor = 0.1;
        return jaro + prefix * scalingFactor * (1 - jaro);
    }

    private static double JaroSimilarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0.0;
        }

        var matchDistance = Math.Max(a.Length, b.Length) / 2 - 1;
        var aMatches = new bool[a.Length];
        var bMatches = new bool[b.Length];

        var matches = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var start = Math.Max(0, i - matchDistance);
            var end = Math.Min(i + matchDistance + 1, b.Length);

            for (var j = start; j < end; j++)
            {
                if (bMatches[j] || a[i] != b[j])
                {
                    continue;
                }

                aMatches[i] = true;
                bMatches[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0)
        {
            return 0.0;
        }

        var k = 0;
        var transpositions = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (!aMatches[i])
            {
                continue;
            }

            while (!bMatches[k])
            {
                k++;
            }

            if (a[i] != b[k])
            {
                transpositions++;
            }

            k++;
        }

        var m = matches;
        return (m / (double)a.Length +
                m / (double)b.Length +
                (m - transpositions / 2.0) / m) / 3.0;
    }

    private sealed class FuzzyIndex
    {
        public Dictionary<string, List<string>> ByLast { get; }
        public Dictionary<char, List<string>> LastByInitial { get; }

        public FuzzyIndex(Dictionary<string, PlayerRecord> db)
        {
            ByLast = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var (key, player) in db)
            {
                var last = LastNameKey(player.Name);
                if (string.IsNullOrWhiteSpace(last))
                {
                    continue;
                }

                if (!ByLast.TryGetValue(last, out var list))
                {
                    list = new List<string>();
                    ByLast[last] = list;
                }

                list.Add(key);
            }

            LastByInitial = new Dictionary<char, List<string>>();
            foreach (var last in ByLast.Keys)
            {
                var initial = last[0];
                if (!LastByInitial.TryGetValue(initial, out var list))
                {
                    list = new List<string>();
                    LastByInitial[initial] = list;
                }

                list.Add(last);
            }
        }
    }

    private sealed class PlayerRecord
    {
        public string Name { get; }
        public string Country { get; }
        public uint FideId { get; }
        public string Bio { get; }
        public List<PlayerRating> Ratings { get; }

        public PlayerRecord(string name, string country, uint fideId, string bio, List<PlayerRating> ratings)
        {
            Name = name;
            Country = country;
            FideId = fideId;
            Bio = bio;
            Ratings = ratings;
        }

        public void SortRatings()
        {
            Ratings.Sort(static (a, b) =>
            {
                var yearCompare = a.Year.CompareTo(b.Year);
                return yearCompare != 0 ? yearCompare : a.Month.CompareTo(b.Month);
            });
        }

        public int? RatingFor(ushort year, byte month)
        {
            if (Ratings.Count == 0)
            {
                return null;
            }

            var idx = BinarySearch(year, month);
            if (idx >= 0)
            {
                return Ratings[idx].Rating;
            }

            // Try previous month.
            if (month > 1)
            {
                idx = BinarySearch(year, (byte)(month - 1));
                if (idx >= 0)
                {
                    return Ratings[idx].Rating;
                }
            }

            // Try next month.
            if (month < 12)
            {
                idx = BinarySearch(year, (byte)(month + 1));
                if (idx >= 0)
                {
                    return Ratings[idx].Rating;
                }
            }

            return null;
        }

        private int BinarySearch(ushort year, byte month)
        {
            var lo = 0;
            var hi = Ratings.Count - 1;

            while (lo <= hi)
            {
                var mid = lo + ((hi - lo) / 2);
                var current = Ratings[mid];

                var cmp = current.Year.CompareTo(year);
                if (cmp == 0)
                {
                    cmp = current.Month.CompareTo(month);
                }

                if (cmp == 0)
                {
                    return mid;
                }

                if (cmp < 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return -1;
        }
    }

    private readonly record struct PlayerRating(ushort Year, byte Month, ushort Rating);
}

