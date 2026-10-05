extern alias HexGame;
using Dingler.Server;
using static HexGame::Game.Shared.Mail.Messages.Mail;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using Dingler.Game.DeckImport;
using Dingler.Game.Services;
using Dingler.Game.Tournaments;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Network.Profile;
using HexGame::Game.Shared.Network.Tournaments;

namespace Dingler.Game.Handlers.Request.Mail;

extern alias HexGame;

[Authenticated]
public sealed class 
	UnreadMailAsyncRequestHandler : IAsyncRequestHandler<GetUnreadMailCount.Request, GetUnreadMailCount.Response>
{
	private readonly DeckService _deckService;
	private readonly CollectionCacheService _collectionCacheService;
	private readonly TournamentManager _tournamentManager;
	private readonly DeckImportService _deckImport;

	public UnreadMailAsyncRequestHandler(DeckService deckService, CollectionCacheService collectionCacheService,
		TournamentManager tournamentManager, DeckImportService deckImport)
	{
		_deckService = deckService;
		_collectionCacheService = collectionCacheService;
		_tournamentManager = tournamentManager;
		_deckImport = deckImport;
	}

	public async Task<GetUnreadMailCount.Response> HandleRequestAsync(SessionContext context,
		GetUnreadMailCount.Request request, CancellationToken token)
	{
		InitTournamentDescriptionsEventArgs tournamentDescriptions =
			_tournamentManager.StartupDescriptions ?? new InitTournamentDescriptionsEventArgs();

		await context.SendMessageToClientAsync(tournamentDescriptions, token);
		
		// Fork: decks dropped into the deck-import inbox are added before the decks are read for the profile stream.
		await _deckImport.ImportInboxAsync(context);

		var deckTask = _deckService.GetPlayerDecksAsync(context.ProfileId);

		await _collectionCacheService.SendProfileStreamAsync(context, deckTask, token);

		var profileUpdateEvent = new ProfileGenericUpdateEventArgs()
		{
			Message = new ProfileGenericMessage()
			{
				Data = context.Encoder.Encode(new ProfileGenericLoginStreamDone())
			}
		};

		await context.SendMessageToClientAsync(profileUpdateEvent, token);
		
		return new GetUnreadMailCount.Response()
		{
			UnreadMailCount = 0,
		};
	}
}