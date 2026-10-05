extern alias HexGame;
using Dingler.Game.Tournaments;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using HexGame::Game.Shared.Network.LoadBalancer;

namespace Dingler.Game.Handlers.Request.Game;

[Authenticated]
public class QuitOnReconnectionGameRequestHandler : IRequestHandler<QuitOnReconnectionGameRequestArgs>
{
	private readonly TournamentManager _tournamentManager;
	private readonly Dingler.Game.Games.GameManager _gameManager;

	public QuitOnReconnectionGameRequestHandler(TournamentManager tournamentManager, Dingler.Game.Games.GameManager gameManager)
	{
		_tournamentManager = tournamentManager;
		_gameManager = gameManager;
	}
	
	public void HandleRequest(SessionContext context, QuitOnReconnectionGameRequestArgs request)
	{
		// Frost Ring Arena: "No" to "reconnect to disconnected arena game?" forfeits that battle.
		if (_gameManager.TryGetGameForPlayer(context.UserName!, out var game) && game.IsArena)
		{
			game.ForfeitArena(Dingler.Game.Arena.ArenaBattleService.HumanId(context));
			return;
		}

		if (!_tournamentManager.TryGetTournamentPlayerIsIn(context.UserName!, out var currentTournamentId))
			return;
		
		if (!_tournamentManager.TryGetTournament(currentTournamentId, out var tournament))
			return;

		tournament.TryForfeitMatch(context.UserName!);
	}
}