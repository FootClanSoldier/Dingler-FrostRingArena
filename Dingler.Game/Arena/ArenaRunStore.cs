using System.Text.Json;
using Dingler.Game.Protocol;

namespace Dingler.Game.Arena;

/// <summary>
/// Arena runs, one JSON file per player (folder/arena-{profileId}.json), kept apart from Dingler's own database.
/// A singleton shared by all sessions, hence the lock. Writes go to a temporary file first, then replace the old one.
/// A damaged file is set aside (renamed .corrupt) and treated as "no run", never thrown.
/// </summary>
public sealed class ArenaRunStore
{
	private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

	private readonly string _folder;
	private readonly object _gate = new();
	private readonly Dictionary<ulong, ArenaRunRecord?> _cache = new();
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
