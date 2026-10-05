extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using Dingler.Game.Arena;
using HexGame::Game.Client.Network.Campaign;
using HexGame::Game.Shared.Network.Campaign;
using HexGame::Reckoning.Campaign.Messages.Arena;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Campaign;

// Frost Ring Arena, 10001: sent every time the arena screen opens. With a stored run: Success = true and the run
// (the client then shows the lobby and bracket). Without one: Success = false, the normal "no run yet" answer, which
// opens Hogarth's welcome window. Every object in the reply is non-null.
[Authenticated]
public sealed class JoinCampaignArenaRequestHandler : IRequestHandler<JoinCampaignArenaRequestArgs, JoinCampaignArenaResponse>
{
	private readonly ArenaRunStore _store;
	private readonly ILogger<JoinCampaignArenaRequestHandler>? _logger;

	public JoinCampaignArenaRequestHandler(ArenaRunStore store, ILogger<JoinCampaignArenaRequestHandler>? logger = null)
	{
		_store = store;
		_logger = logger;
	}

	public JoinCampaignArenaResponse HandleRequest(SessionContext context, JoinCampaignArenaRequestArgs request)
	{
		try
		{
			if (_store.TryGet(context.ProfileId, out var run))
			{
				var current = ArenaReplies.CurrentFight(run);
				_logger?.LogInformation("Arena: {user} opened the arena screen; run {arena}, fight {order}", context.UserName, run.ArenaId, current.Order);
				return new JoinCampaignArenaResponse
				{
					Success = true,
					ArenaInfo = ArenaReplies.ToArenaData(run),
					EncounterData = ArenaReplies.ToFight(run, current),
					ChallengerData = ArenaReplies.ChallengerOf(current),
					MCChallengeData = ArenaReplies.MCChallengeOf(current),
					FightHistory = ArenaReplies.ToHistory(run),
				};
			}
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Arena: reading the run failed for {user}; answering 'no run'", context.UserName);
		}

		_logger?.LogInformation("Arena: {user} opened the arena screen (no run yet)", context.UserName);
		return NoRun();
	}

	private static JoinCampaignArenaResponse NoRun()
	{
		return new JoinCampaignArenaResponse
		{
			Success = false,
			ArenaInfo = ArenaReplies.EmptyArenaData(),
			EncounterData = ArenaReplies.EmptyFight(),
			ChallengerData = ArenaReplies.EmptyChallenger(),
			MCChallengeData = ArenaReplies.EmptyMCChallenge(),
			FightHistory = new List<ArenaFight>(),
		};
	}
}
