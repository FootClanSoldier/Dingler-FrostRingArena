extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using Dingler.Game.Arena;
using HexGame::Game.Shared.Network.Campaign;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Campaign;

// Frost Ring Arena, 10019: the client sends ACCEPT (it never shows the accept/decline pop-up) right before a fight that
// carries Hogarth's challenge. The answer is stored for information only: a challenge applies whenever the fight has
// one (design 03 §4.2). The client expects no reply.
[Authenticated]
public sealed class UpdateMCChallengeRequestHandler : IRequestHandler<UpdateMCChallengeRequestArgs>
{
	private readonly ArenaRunStore _store;
	private readonly ILogger<UpdateMCChallengeRequestHandler>? _logger;

	public UpdateMCChallengeRequestHandler(ArenaRunStore store, ILogger<UpdateMCChallengeRequestHandler>? logger = null)
	{
		_store = store;
		_logger = logger;
	}

	public void HandleRequest(SessionContext context, UpdateMCChallengeRequestArgs request)
	{
		try
		{
			var answer = request.EncounterData?.ChallengeResponse;
			if (answer is not ("ACCEPT" or "DECLINE") || !_store.TryGet(context.ProfileId, out var run))
				return;

			var current = ArenaReplies.CurrentFight(run);
			if (current.Challenge is null) return;
			current.ChallengeResponse = answer;
			_store.Save(run);
			_logger?.LogInformation("Arena: {user} answered {answer} to the challenge of fight {order}", context.UserName, answer, current.Order);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Arena: storing the challenge answer failed for {user}", context.UserName);
		}
	}
}
