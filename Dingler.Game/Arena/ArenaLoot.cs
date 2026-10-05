extern alias HexGame;
using Dingler.Game.Protocol;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Mechanics;
using HexGame::Reckoning.Campaign.Messages.Arena;
using HexGame::Reckoning.Game;

namespace Dingler.Game.Arena;

/// <summary>One reward as the run stores it (design 03 §4.5, D7b-A). Shown and logged only (D7-A): nothing enters the
/// player's collection, which Dingler already fills with every item.</summary>
public sealed class ArenaLootRecord
{
	public ulong FightId { get; set; }
	public string Type { get; set; } = ArenaLoot.Gold;      // GOLD, EQUIPMENT, CARD (Uruunaz's card; SLEEVE in step 5)
	public string Reason { get; set; } = ArenaLoot.Gold;    // GOLD, WIN, BONUS, PERFECT, URUUNAZ, ZAKIIR
	public int Gold { get; set; }
	public string? TemplateId { get; set; }
	public string Rarity { get; set; } = string.Empty;
}

/// <summary>
/// Frost Ring Arena loot (build step 7). The client computes no loot (study 01 §2.10): everything here is the server's.
/// Gold is the 2018 level rebuilt to fit the reported totals (D7b-A: 9,000 for a perfect run). Chests hold equipment
/// only until the arena reward cards are identified in the data; Uruunaz adds his own card. The pool is read from the client's own data: every
/// equipment item whose design notes are "Arena" or "FRA" (423 in client 1.1.0.086; 9 of them end with a line break).
/// Client rules kept here (IL, UIArenaCampaignViewModel): Reason is never null (null would reveal the item in the in-run
/// pop-up instead of a chest); every row carries its fight's FightInstance (the summary counts rewards per tier by it).
/// </summary>
public static class ArenaLoot
{
	public const string Gold = "GOLD";
	public const string Equipment = "EQUIPMENT";
	public const string Card = "CARD";
	public const string Win = "WIN";
	public const string Bonus = "BONUS";
	public const string Perfect = "PERFECT";
	public const string Uruunaz = "URUUNAZ";
	public const string Zakiir = "ZAKIIR";

	private static readonly int[] PouchGold = { 240, 330, 390, 480 };     // per regular win, tiers 1-4
	private static readonly int[] BossGold = { 540, 780, 840, 1080 };     // per boss win
	// Rare opponents (owner's memory, 2026-10-05; adjustable): Uruunaz paid about 50,000 gold and one Leeching Burrower per
	// win (doc 04 had 100,000); Zakiir paid much more than a normal fight but less than Uruunaz [amount rebuilt].
	public const int UruunazGold = 50_000;
	public const int ZakiirGold = 25_000;
	public const string UruunazCardName = "Leeching Burrower";
	private const string ArenaCardSetName = "Set01_PvE_Arena";
	public const double BonusChance = 0.25;                               // the boss "bonus loot roll" [rebuilt]

	// Chest rarity rises by tier, overlapping (owner's choice, 2026-10-05): a uniform draw among the items of these rarities.
	private static readonly ERarity[][] TierRarities =
	{
		new[] { ERarity.Common, ERarity.Uncommon },
		new[] { ERarity.Uncommon, ERarity.Rare },
		new[] { ERarity.Rare },
		new[] { ERarity.Rare, ERarity.Legendary },
	};

	private sealed record PoolItem(ResourceId Id, string Name, ERarity Rarity);

	private static readonly Lazy<IReadOnlyList<PoolItem>> LazyPool = new(BuildPool);

	private static IReadOnlyList<PoolItem> BuildPool()
	{
		var templates = HexGame::Singleton<TemplateManager>.Instance;
		var pool = templates.InventoryItems.Values
			.OfType<InventoryEquipmentData>()
			.Where(e => e.m_DesignNotes?.Trim() is "Arena" or "FRA")
			.OrderBy(e => e.m_Id.m_Guid)
			.Select(e => new PoolItem(e.m_Id, e.m_Name ?? string.Empty, e.m_Rarity))
			.ToList();
		var byRarity = string.Join(", ", pool.GroupBy(i => i.Rarity).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
		StaticLogger.LogInformation("Arena loot pool: {count} items ({rarities}) (expected 423: Common 141, Uncommon 136, Rare 98, Legendary 48)",
			pool.Count, byRarity);
		return pool;
	}

	// The card exists 4 times in the data; the real one is in the arena card set.
	private static readonly Lazy<CardTemplate?> LazyUruunazCard = new(() =>
	{
		var templates = HexGame::Singleton<TemplateManager>.Instance;
		var named = templates.Cards.Values.Where(c => c.m_Name == UruunazCardName).ToList();
		var inSet = named.FirstOrDefault(c => templates.CardSets.TryGetValue(c.m_SetId, out var set) && set.m_Name == ArenaCardSetName);
		var card = inSet ?? named.FirstOrDefault();
		StaticLogger.LogInformation("Arena loot: Uruunaz's card {name} = {id} ({count} named; in the arena set: {inSet}) (expected f6430113-10f1-4908-8bf7-ce25597e968d)",
			UruunazCardName, card?.m_Id.m_Guid.ToString() ?? "NOT FOUND", named.Count, inSet is not null);
		return card;
	});

	/// <summary>The rewards for one won fight. The caller decides the facts (boss, perfect tier, rare opponent).</summary>
	public static List<ArenaLootRecord> ForWin(ArenaFightRecord fight, bool isBoss, bool perfectTier, ArenaOpponent? opponent, Random rng)
	{
		var tier = Math.Clamp(fight.Order / 5, 0, 3);
		var loot = new List<ArenaLootRecord>
		{
			new() { FightId = fight.FightId, Type = Gold, Reason = Gold, Gold = isBoss ? BossGold[tier] : PouchGold[tier] },
		};
		if (isBoss)
		{
			AddChest(loot, fight, tier, Win, rng);
			if (rng.NextDouble() < BonusChance)
				AddChest(loot, fight, tier, Bonus, rng);
			if (perfectTier)
				AddChest(loot, fight, tier, Perfect, rng);
		}
		if (opponent?.Group == ArenaGroup.Rare)
		{
			var isZakiir = opponent.ChampionName.Contains("Zakiir", StringComparison.OrdinalIgnoreCase);
			loot.Add(new() { FightId = fight.FightId, Type = Gold, Reason = isZakiir ? Zakiir : Uruunaz, Gold = isZakiir ? ZakiirGold : UruunazGold });
			if (!isZakiir && LazyUruunazCard.Value is { } card)
				loot.Add(new() { FightId = fight.FightId, Type = Card, Reason = Uruunaz, TemplateId = card.m_Id.m_Guid.ToString(), Rarity = card.m_CardRarity.ToString() });
		}
		return loot;
	}

	private static void AddChest(List<ArenaLootRecord> loot, ArenaFightRecord fight, int tier, string reason, Random rng)
	{
		var rarities = TierRarities[tier];
		var choices = LazyPool.Value.Where(i => rarities.Contains(i.Rarity)).ToList();
		if (choices.Count == 0)
			choices = LazyPool.Value.ToList();
		if (choices.Count == 0)
			return;   // no pool in this client's data: the gold still counts
		var item = choices[rng.Next(choices.Count)];
		loot.Add(new() { FightId = fight.FightId, Type = Equipment, Reason = reason, TemplateId = item.Id.m_Guid.ToString(), Rarity = item.Rarity.ToString() });
	}

	public static string Describe(ArenaLootRecord l)
	{
		if (l.Type == Gold)
			return $"{l.Gold} gold ({l.Reason})";
		var name = l.TemplateId is null ? "?"
			: l.Type == Card ? LazyUruunazCard.Value?.m_Name ?? l.TemplateId
			: LazyPool.Value.FirstOrDefault(i => i.Id.m_Guid.ToString() == l.TemplateId)?.Name ?? l.TemplateId;
		return $"{l.Reason} chest: {name} ({l.Rarity})";
	}

	public static ArenaReward ToReward(ArenaRunRecord run, ArenaLootRecord l) => new()
	{
		ArenaInstance = run.ArenaId,
		FightInstance = l.FightId,
		Type = l.Type,
		Rarity = l.Rarity,
		Gold = l.Gold,
		Reason = l.Reason,
		TemplateID = l.TemplateId is null ? ResourceId.Invalid : new ResourceId(l.TemplateId),
	};
}
