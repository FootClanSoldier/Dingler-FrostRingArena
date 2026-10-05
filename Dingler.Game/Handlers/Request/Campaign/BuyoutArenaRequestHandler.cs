extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using Dingler.Game.Arena;
using HexGame::Game.Client.Network.Campaign;
using HexGame::Game.Shared.Network.Campaign;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Campaign;

// Frost Ring Arena, 10017: "Skip Tier 1" (design 03 D11-A, free). Allowed with the account flag ARENA_TIER1_PERFECT on a
// fresh run (no fight played, not bought out). Tier 1's fights become SKIP: no wins, no rewards (the confirm text
// promises none), and the run can't earn the perfect-clear sleeve. The client stores every field of the reply and
// redraws the lobby from it, waiting for nothing else (ArenaClient.OnArenaBuyoutResponse), so the reply is complete and
// non-null. A refusal only logs on the client; the button stays for a retry. The client's copies are ignored.
[Authenticated]
public sealed class BuyoutArenaRequestHandler : IRequestHandler<BuyoutArenaRequestArgs, BuyoutArenaResponse>
{
	private readonly ArenaRunStore _store;
	private readonly ILogger<BuyoutArenaRequestHandler>? _logger;

	public BuyoutArenaRequestHandler(ArenaRunStore store, ILogger<BuyoutArenaRequestHandler>? logger = null)
	{
		_store = store;
		_logger = logger;
	}

	public BuyoutArenaResponse HandleRequest(SessionContext context, BuyoutArenaRequestArgs request)
	{
		try
		{
			string? refusal = null;
			if (!_store.TryGet(context.ProfileId, out var run) || run.State != ArenaRunRecord.Active)
				refusal = "no active run";
			else if (!_store.HasFlag(context.ProfileId, ArenaAccount.Tier1Perfect))
				refusal = "the account has no " + ArenaAccount.Tier1Perfect;
			else if (run.IsBuyout)
				refusal = "tier 1 is already skipped";
			else if (run.Wins != 0 || run.Loses != 0 || run.Fights.Any(f => f.Order < 5 && f.Result != "NONE"))
				refusal = "a tier-1 fight was already played";

			if (refusal is not null)
			{
				_logger?.LogWarning("Arena: {user} can't skip tier 1 ({reason})", context.UserName, refusal);
				return Refuse();
			}

			foreach (var fight in run.Fights.Where(f => f.Order < 5))
				fight.Result = "SKIP";
			run.IsBuyout = true;
			_store.Save(run);

			var current = ArenaReplies.CurrentFight(run);
			_logger?.LogInformation("Arena: {user} skipped tier 1 of run {arena}; next fight {order}", context.UserName, run.ArenaId, current.Order);
			return new BuyoutArenaResponse
			{
				Success = true,
				ArenaInfo = ArenaReplies.ToArenaData(run),
				EncounterData = ArenaReplies.ToFight(run, current),
				ChallengerData = ArenaReplies.ChallengerOf(current),
				MCChallengeData = ArenaReplies.MCChallengeOf(current),
				FightHistory = ArenaReplies.ToHistory(run),
				Error = EBuyoutArenaError.Ok,
				ErrorMessage = string.Empty,
			};
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Arena: skipping tier 1 failed for {user}", context.UserName);
			return Refuse();
		}
	}

	private static BuyoutArenaResponse Refuse() => new()
	{
		Success = false,
		ArenaInfo = ArenaReplies.EmptyArenaData(),
		EncounterData = ArenaReplies.EmptyFight(),
		ChallengerData = ArenaReplies.EmptyChallenger(),
		MCChallengeData = ArenaReplies.EmptyMCChallenge(),
		FightHistory = new(),
		Error = EBuyoutArenaError.InternalServerError,
		ErrorMessage = string.Empty,
	};
}
