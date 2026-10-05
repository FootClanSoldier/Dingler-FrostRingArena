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

// Frost Ring Arena, 10011: "Withdraw" / "Claim Rewards". Uses the stored run, never the client's copy. With no wins
// (always, until battles exist in build step 4): delete the run, unlock the deck, and reply FailedNoWins (2); the client
// shows "No Arena Wins" and returns to the landing page without sending DestroyArenaData.
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
			// DestroyArenaData). Loot itself is build step 7: until then the reward list is empty. A repeat gets the same.
			if (run.State != ArenaRunRecord.CashedOut)
			{
				run.State = ArenaRunRecord.CashedOut;
				_store.Save(run);
				_logger?.LogInformation("Arena: {user} cashed out run {arena} with {wins} wins and {loses} strikes (no loot yet)",
					context.UserName, run.ArenaId, run.Wins, run.Loses);
			}
			return new DoArenaCashOutResponse
			{
				Success = true,
				GoldWin = 0,
				AllLoot = new List<ArenaReward>(),
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
