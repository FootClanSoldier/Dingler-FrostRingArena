extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using Dingler.Game.Arena;
using HexGame::Game.Client.Network.Campaign;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Mechanics;
using HexGame::Game.Shared.Network.Campaign;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Campaign;

// Frost Ring Arena, 10003: the player picked a deck in the welcome window. Build and save a new run (bracket per the
// owner's decisions, ArenaBracketBuilder), lock the deck to the arena, and reply with the run. DeckID is the deck's
// instance id, the same key as SessionContext.Decks; ArenaData.DeckId must be exactly this value, or the client's
// lobby crashes looking the deck up. Never throws: any failure replies FailCreateInstance with a text key.
[Authenticated]
public sealed class AssignArenaDeckRequestHandler : IRequestHandler<AssignArenaDeckRequestArgs, AssignArenaDeckResponse>
{
	private readonly ArenaRunStore _store;
	private readonly ILogger<AssignArenaDeckRequestHandler>? _logger;

	public AssignArenaDeckRequestHandler(ArenaRunStore store, ILogger<AssignArenaDeckRequestHandler>? logger = null)
	{
		_store = store;
		_logger = logger;
	}

	public AssignArenaDeckResponse HandleRequest(SessionContext context, AssignArenaDeckRequestArgs request)
	{
		try
		{
			if (_store.TryGet(context.ProfileId, out var existing))
			{
				_logger?.LogInformation("Arena: {user} already has run {arena}; sending it again", context.UserName, existing.ArenaId);
				return Reply(existing);
			}

			if (!context.Decks.TryGetValue(request.DeckID, out var deck))
				return Failure(context, $"deck {request.DeckID} not found in the player's decks");
			if (deck.PVEChampionId > 0)
				return Failure(context, $"deck {request.DeckID} uses a PvE champion");
			LogValidation(context, deck);

			var arenaId = _store.NewArenaId();
			var run = new ArenaRunRecord
			{
				ArenaId = arenaId,
				ProfileId = context.ProfileId,
				DeckId = request.DeckID,
				CreatedUtc = DateTime.UtcNow,
				Fights = ArenaBracketBuilder.Build(arenaId, new Random(unchecked((int)arenaId))),
			};
			_store.Save(run);

			deck.Lock = EDeckLock.Arena_Lock;
			deck.LockHolder = arenaId;

			_logger?.LogInformation("Arena: {user} started run {arena} with deck '{deck}': {bracket}", context.UserName, arenaId,
				deck.DeckName, string.Join(" | ", run.Fights.Select(Describe)));
			return Reply(run);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Arena: starting a run failed for {user}", context.UserName);
			return Failure(context, ex.Message);
		}
	}

	private static AssignArenaDeckResponse Reply(ArenaRunRecord run)
	{
		var current = ArenaReplies.CurrentFight(run);
		return new AssignArenaDeckResponse
		{
			Success = true,
			ArenaInfo = ArenaReplies.ToArenaData(run),
			EncounterData = ArenaReplies.ToFight(run, current),
			ChallengerData = ArenaReplies.ChallengerOf(current),
			FightHistory = ArenaReplies.ToHistory(run),
			Error = EAssignArenaDeckError.Ok,
		};
	}

	private AssignArenaDeckResponse Failure(SessionContext context, string reason)
	{
		_logger?.LogWarning("Arena: could not start a run for {user}: {reason}", context.UserName, reason);
		return new AssignArenaDeckResponse
		{
			Success = false,
			ArenaInfo = ArenaReplies.EmptyArenaData(),
			EncounterData = ArenaReplies.EmptyFight(),
			ChallengerData = ArenaReplies.EmptyChallenger(),
			FightHistory = new List<HexGame::Reckoning.Campaign.Messages.Arena.ArenaFight>(),
			Error = EAssignArenaDeckError.FailCreateInstance,
			ErrorMessage = "ArenaPVE_Error_AssignDeck",   // a text key: the client shows "Unable to create arena instance with selected Deck..."
		};
	}

	// The client only offers decks valid for PvE (format 2047, play format 9). Check again with the game's own validator,
	// but only log the outcome for now: whether it judges Dingler's stored decks correctly is still unverified.
	private void LogValidation(SessionContext context, deck_bits deck)
	{
		try
		{
			var validator = new DeckValidator(deck, new Format(ESetFormat.PvE, EPlayFormat.PvEConstructed), null, (_, _) => true, false, null);
			if (validator.Valid)
				_logger?.LogInformation("Arena: deck '{deck}' passes the PvE deck check", deck.DeckName);
			else
				_logger?.LogWarning("Arena: deck '{deck}' fails the PvE deck check ({errors}); allowed for now",
					deck.DeckName, string.Join(", ", validator.Errors.Keys));
		}
		catch (Exception ex)
		{
			_logger?.LogWarning("Arena: the PvE deck check threw for {user}: {error}", context.UserName, ex.Message);
		}
	}

	private static string Describe(ArenaFightRecord f)
	{
		var o = ArenaRoster.Find(f.ChallengerId);
		return $"{f.Order}:{o?.ChampionName ?? f.ChallengerId.ToString()}{(f.Challenge is null ? "" : " [challenge]")}";
	}
}
