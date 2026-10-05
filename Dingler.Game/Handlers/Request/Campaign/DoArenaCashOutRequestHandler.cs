extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using Dingler.Game.Arena;
using HexGame::Game.Client.Network.Campaign;
using HexGame::Game.Shared.Mechanics;
using HexGame::Game.Shared.Network.Campaign;
using HexGame::Reckoning.Campaign.Messages.Arena;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Campaign;

// Frost Ring Arena, 10011: "Withdraw" / "Claim Rewards". Uses the stored run, never the client's copy. With no wins:
// delete the run, unlock the deck, and reply FailedNoWins (2); the client shows "No Arena Wins" and returns to the landing
// page without sending DestroyArenaData. With wins: AllLoot is every reward of the run (the final window and the summary
// read only this list, not the in-run pushes); GoldWin is only shown in the client's debug text.
[Authenticated]
public sealed class DoArenaCashOutRequestHandler : IRequestHandler<DoArenaCashOutRequestArgs, DoArenaCashOutResponse>
{
	private readonly ArenaRunStore _store;
	private readonly ILogger<DoArenaCashOutRequestHandler>? _logger;

	public DoArenaCashOutRequestHandler(ArenaRunStore store, ILogger<DoArenaCashOutRequestHandler>? logger = null)
	{
		_store = store;
		_logger = logger;
	}

	public DoArenaCashOutResponse HandleRequest(SessionContext context, DoArenaCashOutRequestArgs request)
	{
		try
		{
			if (!_store.TryGet(context.ProfileId, out var run) || run.Wins == 0)
			{
				if (run is not null)
				{
					_store.Delete(context.ProfileId);
					ArenaDeckLock.Release(context, run.DeckId);
					_logger?.LogInformation("Arena: {user} withdrew from run {arena} with no wins; run deleted, deck unlocked", context.UserName, run.ArenaId);
				}
				return Reply(EDoArenaCashOutError.FailedNoWins);
			}

			// With wins: the run becomes CashedOut (the client then shows its summary and loot windows and finally sends
			// DestroyArenaData). Nothing enters the collection (D7-A). A repeat gets the same list.
			var gold = run.Loot.Sum(l => l.Gold);
			// Sleeves (step 5) are paid here only, never pushed during the run (the in-run pop-up throws on them).
			var sleeves = ArenaAccount.SleevesFor(run);
			if (run.State != ArenaRunRecord.CashedOut)
			{
				run.State = ArenaRunRecord.CashedOut;
				_store.Save(run);
				_logger?.LogInformation("Arena: {user} cashed out run {arena} with {wins} wins and {loses} strikes: {gold} gold, {chests} chest(s), sleeves: {sleeves}",
					context.UserName, run.ArenaId, run.Wins, run.Loses, gold, run.Loot.Count(l => l.Type != ArenaLoot.Gold),
					sleeves.Count == 0 ? "none" : string.Join(", ", sleeves.Select(ArenaAccount.SleeveName)));
			}
			return new DoArenaCashOutResponse
			{
				Success = true,
				GoldWin = gold,
				AllLoot = run.Loot.Select(l => ArenaLoot.ToReward(run, l)).Concat(sleeves).ToList(),
				Error = EDoArenaCashOutError.Ok,
				ErrorMessage = string.Empty,
			};
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Arena: cash-out failed for {user}", context.UserName);
			return Reply(EDoArenaCashOutError.FailedToRewardItems);
		}
	}

	private static DoArenaCashOutResponse Reply(EDoArenaCashOutError error) => new()
	{
		Success = false,
		GoldWin = 0,
		AllLoot = new List<ArenaReward>(),
		Error = error,
		ErrorMessage = string.Empty,
	};
}

/// <summary>Keeps the player's cached deck in step with the arena lock (the client reads deck_bits.Lock).</summary>
public static class ArenaDeckLock
{
	public static void Release(SessionContext context, ulong deckId)
	{
		if (context.Decks.TryGetValue(deckId, out var deck) && deck.Lock == EDeckLock.Arena_Lock)
		{
			deck.Lock = EDeckLock.Not_Locked;
			deck.LockHolder = 0;
		}
	}
}
