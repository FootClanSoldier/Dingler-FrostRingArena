extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using Dingler.Game.Arena;
using HexGame::Game.Client.Network.Campaign;
using HexGame::Game.Shared.Network.Campaign;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Campaign;

// Frost Ring Arena, 10027: sent only when the player rejoins a disconnected arena battle. The client rebuilds Hogarth's
// objective panel from the reply (heading and objective as literal text). Always non-null; no challenge = invalid id.
[Authenticated]
public sealed class GetArenaMCChallengeRequestHandler : IRequestHandler<GetArenaMCChallengeRequestArgs, GetArenaMCChallengeResponse>
{
	private readonly ArenaRunStore _store;
	private readonly ILogger<GetArenaMCChallengeRequestHandler>? _logger;

	public GetArenaMCChallengeRequestHandler(ArenaRunStore store, ILogger<GetArenaMCChallengeRequestHandler>? logger = null)
	{
		_store = store;
		_logger = logger;
	}

	public GetArenaMCChallengeResponse HandleRequest(SessionContext context, GetArenaMCChallengeRequestArgs request)
	{
		try
		{
			if (_store.TryGet(context.ProfileId, out var run))
				return new GetArenaMCChallengeResponse { MCChallenge = ArenaReplies.MCChallengeOf(ArenaReplies.CurrentFight(run)) };
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Arena: GetArenaMCChallenge failed for {user}", context.UserName);
		}
		return new GetArenaMCChallengeResponse { MCChallenge = ArenaReplies.EmptyMCChallenge() };
	}
}
