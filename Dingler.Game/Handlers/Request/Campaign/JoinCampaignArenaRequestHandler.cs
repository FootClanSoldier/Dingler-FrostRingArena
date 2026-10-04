extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using HexGame::Game.Client.Network.Campaign;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Mechanics;
using HexGame::Game.Shared.Network.Campaign;
using HexGame::Reckoning.Campaign.Messages.Arena;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Campaign;

// Frost Ring Arena, 10001: sent every time the arena screen opens. Success = false is the normal "no run yet"
// answer, which opens Hogarth's welcome window. Runs don't exist yet (build step 3), so the answer is always that.
// Every object in the reply is non-null, as the client expects of a run-carrying reply.
[Authenticated]
public sealed class JoinCampaignArenaRequestHandler : IRequestHandler<JoinCampaignArenaRequestArgs, JoinCampaignArenaResponse>
{
	private readonly ILogger<JoinCampaignArenaRequestHandler>? _logger;

	public JoinCampaignArenaRequestHandler(ILogger<JoinCampaignArenaRequestHandler>? logger = null)
	{
		_logger = logger;
	}

	public JoinCampaignArenaResponse HandleRequest(SessionContext context, JoinCampaignArenaRequestArgs request)
	{
		_logger?.LogInformation("Arena: {user} opened the arena screen (no run yet)", context.UserName);

		return new JoinCampaignArenaResponse
		{
			Success = false,
			ArenaInfo = new ArenaData { GameMode = ECampaignDifficulty.NORMAL, Buffs = new List<ArenaBuff>() },
			EncounterData = new ArenaFight { FightResults = "NONE", RoundChallenge = ResourceId.Invalid, ChallengeResponse = "NONE" },
			ChallengerData = new ArenaChallenger { ChallengerName = string.Empty, IsBoss = "false", Equipment = new List<ResourceId>() },
			MCChallengeData = new ArenaMCChallenge { TemplateID = ResourceId.Invalid, Header = string.Empty, Body = string.Empty },
			FightHistory = new List<ArenaFight>(),
		};
	}
}
