extern alias HexGame;
using Dingler.Game.Protocol;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Resources;
using HexGame::Reckoning.Campaign.Messages.Arena;
using HexGame::Reckoning.Game;
using HexGame::Reckoning.Profile.Messages;

namespace Dingler.Game.Arena;

/// <summary>
/// Build step 5: the arena's permanent account flags and its 5 sleeves (design 03 D11-A, D12-A, D16-A).
/// The client reads only ARENA_TIER1_PERFECT (it shows "Skip Tier 1", UIArenaCampaignViewModel.SetBuyoutButton: the flag
/// exists by name, not bought out, Wins = 0, Loses = 0, tier 1); the others are kept as a record. The client's flag list
/// is replaced whole by event 2201 (PlayerProfile.UserFlagsUpdatedEvent), so the full list is always sent.
/// Sleeves are paid at cash-out only, every time earned (owner's choice, 2026-10-05), never in a LootUpdate: the in-run
/// pop-up's sleeve branch throws on a repeated reason. The final window (SetupLootWindow) needs a real deck-sleeve
/// TemplateID (anything else throws in a bad String.Format) and never the same sleeve with the same reason twice.
/// </summary>
public static class ArenaAccount
{
	public const string Tier1Perfect = BuiltInResources.AccountFlag.ARENA_TIER1_PERFECT;
	public const string Cleared = BuiltInResources.AccountFlag.ARENA_WIN20;            // "ARENA_CLEARED"
	public const string PerfectClear = BuiltInResources.AccountFlag.ARENA_WIN20PERFECT; // "ARENA_PERFECT_CLEAR"
	public const string UruunazFlag = BuiltInResources.AccountFlag.ARENA_URUUNAZ;
	public const string ZakiirFlag = BuiltInResources.AccountFlag.ARENA_ZAKIIR;
	public const string HogarthFlag = BuiltInResources.AccountFlag.ARENA_HOGARTH;

	// Sleeve -> the reason it is paid with. The client knows ARENA_CLEARED, ARENA_PERFECT_CLEAR, ARENA_URUUNAZ and ZAKIIR as
	// sleeve reasons; Hogarth has none in the client (D16-A: his flag name; the final window shows any reason the same way).
	private static readonly (string Sleeve, string Reason)[] SleeveReasons =
	{
		("Frost Ring Arena Sleeve", Cleared),
		("Frost Ring Arena Perfect Sleeve", PerfectClear),
		("Hogarth Sleeve", HogarthFlag),
		("Uruunaz Sleeve", UruunazFlag),
		("Matriarch of Flames Sleeve", ArenaLoot.Zakiir),
	};

	private static readonly Lazy<Dictionary<string, ResourceId>> LazySleeves = new(() =>
	{
		var templates = HexGame::Singleton<TemplateManager>.Instance;
		var byName = templates.InventoryItems.Values.OfType<InventoryDeckSleeve>()
			.Where(s => s.m_Name != null)
			.GroupBy(s => s.m_Name)
			.ToDictionary(g => g.Key, g => g.First().m_Id);
		var found = new Dictionary<string, ResourceId>();
		foreach (var (sleeve, _) in SleeveReasons)
			if (byName.TryGetValue(sleeve, out var id))
				found[sleeve] = id;
		StaticLogger.LogInformation("Arena sleeves: {found} of {total} found in the data ({missing})", found.Count, SleeveReasons.Length,
			found.Count == SleeveReasons.Length ? "none missing" : "missing: " + string.Join(", ", SleeveReasons.Select(r => r.Sleeve).Where(n => !found.ContainsKey(n))));
		return found;
	});

	public static bool IsHogarth(ArenaOpponent? o) => o?.ChampionName.Contains("Hogarth", StringComparison.OrdinalIgnoreCase) == true;
	public static bool IsZakiir(ArenaOpponent? o) => o?.Group == ArenaGroup.Rare && o.ChampionName.Contains("Zakiir", StringComparison.OrdinalIgnoreCase);
	public static bool IsUruunaz(ArenaOpponent? o) => o?.Group == ArenaGroup.Rare && !IsZakiir(o);

	/// <summary>The flags a won fight earns, given the run state after the win was recorded.</summary>
	public static List<string> FlagsForWin(ArenaRunRecord run, ArenaFightRecord fight)
	{
		var flags = new List<string>();
		var opponent = ArenaRoster.Find(fight.ChallengerId);
		if (fight.Order == 4 && (run.LastTierLoss & 2) == 0 && !run.IsBuyout)
			flags.Add(Tier1Perfect);
		if (fight.Order == 19)
		{
			flags.Add(Cleared);
			if (IsPerfectRun(run))
				flags.Add(PerfectClear);
		}
		if (IsHogarth(opponent)) flags.Add(HogarthFlag);
		if (IsUruunaz(opponent)) flags.Add(UruunazFlag);
		if (IsZakiir(opponent)) flags.Add(ZakiirFlag);
		return flags;
	}

	/// <summary>Cleared with no loss ever recorded, and tier 1 not skipped (D11-A, D12-A).</summary>
	public static bool IsPerfectRun(ArenaRunRecord run) => run.LastTierLoss == 0 && !run.IsBuyout;

	/// <summary>The sleeves a run has earned, as reward rows. FightInstance is the fight that earned each (a real fight
	/// with a known challenger, so the final window's opponent lookup is safe). One row per sleeve and reason at most.</summary>
	public static List<ArenaReward> SleevesFor(ArenaRunRecord run)
	{
		var rows = new List<(string Sleeve, ulong FightId)>();
		var final = run.Fights.FirstOrDefault(f => f.Order == 19 && f.Result == "WIN");
		if (final is not null)
		{
			rows.Add(("Frost Ring Arena Sleeve", final.FightId));
			if (IsPerfectRun(run))
				rows.Add(("Frost Ring Arena Perfect Sleeve", final.FightId));
		}
		foreach (var f in run.Fights.Where(f => f.Result == "WIN").OrderBy(f => f.Order))
		{
			var o = ArenaRoster.Find(f.ChallengerId);
			if (IsHogarth(o)) rows.Add(("Hogarth Sleeve", f.FightId));
			if (IsUruunaz(o)) rows.Add(("Uruunaz Sleeve", f.FightId));
			if (IsZakiir(o)) rows.Add(("Matriarch of Flames Sleeve", f.FightId));
		}

		var sleeves = LazySleeves.Value;
		var result = new List<ArenaReward>();
		var seen = new HashSet<string>();
		foreach (var (sleeve, fightId) in rows)
		{
			var reason = SleeveReasons.First(r => r.Sleeve == sleeve).Reason;
			if (!sleeves.TryGetValue(sleeve, out var id) || !seen.Add(sleeve + "|" + reason))
				continue;
			result.Add(new ArenaReward
			{
				ArenaInstance = run.ArenaId,
				FightInstance = fightId,
				Type = "SLEEVE",
				Rarity = string.Empty,
				Gold = 0,
				Reason = reason,
				TemplateID = id,
			});
		}
		return result;
	}

	public static string SleeveName(ArenaReward r) =>
		LazySleeves.Value.FirstOrDefault(kv => kv.Value.Equals(r.TemplateID)).Key ?? r.TemplateID.ToString();

	public static List<FlagData> ToFlagData(IEnumerable<string> flags) =>
		flags.Select(f => new FlagData { Name = f, Progress = 1, Maximum = 1, Completed = true }).ToList();
}
