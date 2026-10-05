using System.Text.Json;

namespace Dingler.Game.DeckImport;

/// <summary>
/// The Hex Codex site's permanent ids, read from its own data files (never typed by hand): data/ids.json maps each site
/// id to the game's GUID and a kind ("card", "champion", "gem", "item-equipment", ...); data/gems.json gives each gem's
/// game type name, which is the EGemTypesNew member name (all 73 match). Loaded once from DeckImport:SiteDataPath.
/// </summary>
public sealed class SiteIds
{
	public sealed record Id(Guid Guid, string Kind, string Name);

	private readonly Dictionary<int, Id> _ids = new();
	private readonly Dictionary<Guid, string> _gemTypes = new();

	public SiteIds(string dataFolder)
	{
		using (var ids = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataFolder, "ids.json"))))
		{
			foreach (var e in ids.RootElement.GetProperty("entries").EnumerateArray())
			{
				var site = e[0].GetInt32();
				if (Guid.TryParse(e[1].GetString(), out var guid))
					_ids[site] = new Id(guid, e[2].GetString() ?? "", e.GetArrayLength() > 3 ? e[3].GetString() ?? "" : "");
			}
		}
		using (var gems = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataFolder, "gems.json"))))
		{
			foreach (var list in gems.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array))
				foreach (var g in list.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
					if (g.TryGetProperty("id", out var id) && g.TryGetProperty("type", out var type) &&
					    Guid.TryParse(id.GetString(), out var guid) && type.GetString() is { Length: > 0 } t)
						_gemTypes[guid] = t;
		}
	}

	public int Count => _ids.Count;
	public int GemCount => _gemTypes.Count;

	public Id? Find(int siteId) => _ids.GetValueOrDefault(siteId);

	public string? GemType(Guid gem) => _gemTypes.GetValueOrDefault(gem);
}
