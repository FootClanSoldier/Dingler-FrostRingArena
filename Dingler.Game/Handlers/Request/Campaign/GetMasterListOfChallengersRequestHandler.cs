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

// Frost Ring Arena, 10007: the full list of arena opponents. The lobby waits for this reply (an unanswered one hangs
// the screen) and it must hold every challenger, or the final loot window crashes.
[Authenticated]
public sealed class GetMasterListOfChallengersRequestHandler : IRequestHandler<GetMasterListOfChallengersRequestArgs, GetMasterListOfChallengersResponse>
{
	private readonly ILogger<GetMasterListOfChallengersRequestHandler>? _logger;

	public GetMasterListOfChallengersRequestHandler(ILogger<GetMasterListOfChallengersRequestHandler>? logger = null)
	{
		_logger = logger;
	}

	public GetMasterListOfChallengersResponse HandleRequest(SessionContext context, GetMasterListOfChallengersRequestArgs request)
	{
		var challengers = ArenaCatalog.Challengers;
		_logger?.LogInformation("Arena: sent {count} challengers to {user}", challengers.Count, context.UserName);

		return new GetMasterListOfChallengersResponse
		{
			Success = true,
			Challengers = challengers.ToList(),
		};
	}
}
