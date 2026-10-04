extern alias HexGame;
using HexGame::Game.Shared;
using HexGame::Reckoning.Campaign.Messages.Arena;
using HexGame::Reckoning.Game;

namespace Dingler.Game.Arena;

/// <summary>
/// The Frost Ring Arena opponents, read from the client's own game data (never typed by hand): every DeckTemplate
/// whose name starts with "Arena_" (109 in client 1.1.0.086). Each gets a fixed challenger id (1..n, in deck-id
/// order) used in ArenaFight.ChallengerInstance and the master list (10007). The client takes a slot's portrait
/// from the deck's champion and needs every challenger in the list, or its final loot window crashes.
/// </summary>
public static class ArenaCatalog
{
	private static readonly Lazy<IReadOnlyList<ArenaChallenger>> LazyChallengers = new(Build);

	public static IReadOnlyList<ArenaChallenger> Challengers => LazyChallengers.Value;

	private static IReadOnlyList<ArenaChallenger> Build()
	{
		var templates = HexGame::Singleton<TemplateManager>.Instance;
		return templates.Decks.Values
			.Where(d => d.m_DeckName != null && d.m_DeckName.StartsWith("Arena_", StringComparison.Ordinal))
			.OrderBy(d => d.m_Id.ToString(), StringComparer.Ordinal)
			.Select((deck, i) => new ArenaChallenger
			{
				ChallengerID = (ulong)(i + 1),
				EncounterDeck = deck.m_Id,
				ChallengerName = templates.Champions.TryGetValue(deck.m_ChampionId, out var champion) && !string.IsNullOrEmpty(champion.m_Name)
					? champion.m_Name
					: deck.m_DeckName["Arena_".Length..],
				IsBoss = "false",       // never read by the client; bosses are decided by bracket position
				Equipment = new List<ResourceId>(),
			})
			.ToList();
	}
}
