using System.Text.Json;
using Dingler.Game.Protocol;

namespace Dingler.Game.Arena;

/// <summary>
/// Arena runs, one JSON file per player (folder/arena-{profileId}.json), kept apart from Dingler's own database.
/// A singleton shared by all sessions, hence the lock. Writes go to a temporary file first, then replace the old one.
/// A damaged file is set aside (renamed .corrupt) and treated as "no run", never thrown.
/// Also keeps each player's permanent arena account flags (folder/flags-{profileId}.json, step 5): ARENA_TIER1_PERFECT and
/// the rest, which outlive runs. The client knows them only from the login profile stream and event 2201.
/// </summary>
public sealed class ArenaRunStore
{
	private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

	private readonly string _folder;
	private readonly object _gate = new();
	private readonly Dictionary<ulong, ArenaRunRecord?> _cache = new();
	private readonly Dictionary<ulong, SortedSet<string>> _flags = new();
	private ulong _lastArenaId;

	public ArenaRunStore(string folder)
	{
		_folder = folder;
	}

	public bool TryGet(ulong profileId, out ArenaRunRecord run)
	{
		lock (_gate)
		{
			if (!_cache.TryGetValue(profileId, out var cached))
				_cache[profileId] = cached = Load(profileId);
			run = cached!;
			return cached is not null;
		}
	}

	/// <summary>True while the deck belongs to a run that hasn't been destroyed (it is locked to the arena).</summary>
	public bool IsDeckInRun(ulong profileId, ulong deckId) => TryGet(profileId, out var run) && run.DeckId == deckId;

	public void Save(ArenaRunRecord run)
	{
		lock (_gate)
		{
			Directory.CreateDirectory(_folder);
			var path = PathFor(run.ProfileId);
			var tmp = path + ".tmp";
			File.WriteAllText(tmp, JsonSerializer.Serialize(run, Json));
			File.Move(tmp, path, overwrite: true);
			_cache[run.ProfileId] = run;
		}
	}

	public void Delete(ulong profileId)
	{
		lock (_gate)
		{
			var path = PathFor(profileId);
			if (File.Exists(path)) File.Delete(path);
			_cache[profileId] = null;
		}
	}

	/// <summary>The player's arena account flags (a copy; empty when none).</summary>
	public IReadOnlyList<string> GetFlags(ulong profileId)
	{
		lock (_gate)
			return FlagsOf(profileId).ToList();
	}

	public bool HasFlag(ulong profileId, string flag)
	{
		lock (_gate)
			return FlagsOf(profileId).Contains(flag);
	}

	/// <summary>Adds the flags and saves. Returns the ones that are new (empty when the player had them all).</summary>
	public List<string> AddFlags(ulong profileId, IEnumerable<string> flags)
	{
		lock (_gate)
		{
			var set = FlagsOf(profileId);
			var added = flags.Where(set.Add).ToList();
			if (added.Count > 0)
			{
				Directory.CreateDirectory(_folder);
				var path = FlagsPathFor(profileId);
				var tmp = path + ".tmp";
				File.WriteAllText(tmp, JsonSerializer.Serialize(set, Json));
				File.Move(tmp, path, overwrite: true);
			}
			return added;
		}
	}

	private SortedSet<string> FlagsOf(ulong profileId)
	{
		if (_flags.TryGetValue(profileId, out var set))
			return set;
		set = new SortedSet<string>(StringComparer.Ordinal);
		var path = FlagsPathFor(profileId);
		if (File.Exists(path))
		{
			try
			{
				foreach (var flag in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new List<string>())
					set.Add(flag);
			}
			catch (Exception ex)
			{
				StaticLogger.LogError("Arena: flags file {path} is damaged ({error}); set aside as .corrupt", path, ex.Message);
				try { File.Move(path, path + ".corrupt", overwrite: true); } catch { /* leave it */ }
			}
		}
		return _flags[profileId] = set;
	}

	private string FlagsPathFor(ulong profileId) => Path.Combine(_folder, $"flags-{profileId}.json");

	/// <summary>A new arena id: milliseconds since 1970, always increasing (and so never 0).</summary>
	public ulong NewArenaId()
	{
		lock (_gate)
		{
			var id = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			if (id <= _lastArenaId) id = _lastArenaId + 1;
			return _lastArenaId = id;
		}
	}

	private string PathFor(ulong profileId) => Path.Combine(_folder, $"arena-{profileId}.json");

	private ArenaRunRecord? Load(ulong profileId)
	{
		var path = PathFor(profileId);
		if (!File.Exists(path)) return null;
		try
		{
			var run = JsonSerializer.Deserialize<ArenaRunRecord>(File.ReadAllText(path));
			if (run is not null && run.Fights.Count == 20) return run;
			throw new InvalidDataException("not a 20-fight run");
		}
		catch (Exception ex)
		{
			StaticLogger.LogError("Arena: run file {path} is damaged ({error}); set aside as .corrupt", path, ex.Message);
			try { File.Move(path, path + ".corrupt", overwrite: true); } catch { /* leave it */ }
			return null;
		}
	}
}
