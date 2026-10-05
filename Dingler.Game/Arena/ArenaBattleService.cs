extern alias HexGame;
using System.Collections.Concurrent;
using Dingler.Server;
using Dingler.Game.Games;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Mechanics;
using HexGame::Reckoning.Game;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Arena;

/// <summary>
/// Frost Ring Arena battles (build step 4). The client asks StartEncounter (the server reserves a battle for the run's
/// current fight), then JoinSession (the server builds and starts the game), then ReadyToStartGame (events flow).
/// The result goes into the run when the engine reports the game's end (design 03 §4.5, D5-A, D15-A).
/// </summary>
public sealed class ArenaBattleService
{
	/// <summary>A reserved battle. Refusal is set when it can't be played: the client is told at JoinSession, where a
	/// refusal shows "unable to start" and goes back (a refused StartEncounter would leave it on its loading screen).</summary>
	public sealed record PendingBattle(ulong GameId, ulong ArenaId, ulong FightId, ResourceId AiDeck, SessionState SessionState, string? Refusal);

	public sealed class ActiveBattle
	{
		public HexGameWrapper? Game { get; set; }
		public required UID Human { get; init; }
		public required UID Ai { get; init; }
		public required ulong ArenaId { get; init; }
		public required ulong FightId { get; init; }
		public int Applied;
	}

	private readonly ArenaRunStore _store;
	private readonly ILogger<ArenaBattleService>? _logger;
	private readonly ConcurrentDictionary<ulong, PendingBattle> _pending = new();   // by profile id
	private readonly ConcurrentDictionary<ulong, ActiveBattle> _active = new();     // by profile id

	public ArenaBattleService(ArenaRunStore store, ILogger<ArenaBattleService>? logger = null)
	{
		_store = store;
		_logger = logger;
	}

	public static UID HumanId(SessionContext context) => new(UID.Type.ServicePlayer, context.ProfileId);

	/// <summary>StartEncounter (22017): reserve a battle for the run's current fight. The client's ArenaInstance and
	/// ArenaOwner are ignored: the stored run decides. Always returns a reservation; check its Refusal.</summary>
	public PendingBattle Reserve(SessionContext context, GameManager games, string sessionName)
	{
		string? refusal = null;
		ArenaFightRecord? fight = null;
		ArenaOpponent? opponent = null;
		var hasRun = _store.TryGet(context.ProfileId, out var run) && run.State == ArenaRunRecord.Active;
		if (!hasRun)
			refusal = "no active arena run";
		else if (run.Loses >= 3)
			refusal = "the run already has 3 strikes";
		else if (games.TryGetGameForPlayer(context.UserName!, out _))
			refusal = "the player's previous battle is still running";
		else
		{
			fight = ArenaReplies.CurrentFight(run);
			opponent = ArenaRoster.Find(fight.ChallengerId);
			if (fight.Result != "NONE" || opponent is null)
				refusal = "the run has no fight left to play";
		}

		var gameId = games.NextGameId();
		var encounter = new SessionStateEncounterData
		{
			SessionFlags = ESessionFlags.IsPvEArena,   // 128 only, as the client sends
			ArenaInstance = hasRun ? run.ArenaId : 0,
			ArenaOwner = context.ProfileId,
			MatchPreviousWinners = new List<ulong>(),
			FirstPlayer = UID.Invalid,
		};
		var state = new SessionState
		{
			SessionId = new UID(UID.Type.AuthoritativeSession, gameId),
			SessionName = sessionName,
			MinimumPlayerCount = 2,
			MaximumPlayerCount = 2,
			EncounterData = encounter,
			JoinInsteadOfReconnect = false,
		};
		var pending = new PendingBattle(gameId, hasRun ? run.ArenaId : 0, fight?.FightId ?? 0,
			opponent?.Challenger.EncounterDeck ?? ResourceId.Invalid, state, refusal);
		_pending[context.ProfileId] = pending;
		if (refusal is null)
			_logger?.LogInformation("Arena: {user} reserved battle {game}: fight {order} against {opponent}",
				context.UserName, gameId, fight!.Order, opponent!.ChampionName);
		else
			_logger?.LogWarning("Arena: {user} can't battle ({reason}); JoinSession will refuse", context.UserName, refusal);
		return pending;
	}

	/// <summary>JoinSession (22021): build and start the reserved battle.</summary>
	public bool TryStart(SessionContext context, GameManager games, ulong sessionInstanceId, ulong deckId,
		List<ETurnPhases>? selfStops, List<ETurnPhases>? opponentStops, out PendingBattle pending, out string reason)
	{
		if (!_pending.TryRemove(context.ProfileId, out pending!) || pending.GameId != sessionInstanceId)
			return Fail("no reserved battle for this session", out reason);
		if (pending.Refusal is not null)
			return Fail(pending.Refusal, out reason);
		if (!_store.TryGet(context.ProfileId, out var run) || run.State != ArenaRunRecord.Active || run.ArenaId != pending.ArenaId)
			return Fail("the run changed since the battle was reserved", out reason);
		if (deckId != run.DeckId)
			return Fail($"deck {deckId} is not the run's deck {run.DeckId}", out reason);
		if (!context.Decks.TryGetValue(run.DeckId, out var deck))
			return Fail($"deck {run.DeckId} not found in the player's decks", out reason);

		var battle = new ActiveBattle
		{
			Human = HumanId(context),
			Ai = new UID(UID.Type.ServiceAI, pending.GameId),
			ArenaId = run.ArenaId,
			FightId = pending.FightId,
		};
		var profileId = context.ProfileId;
		var userName = context.UserName!;

		// Published before the game exists, so the end-of-game callback always finds (and removes) it.
		_active[profileId] = battle;
		try
		{
			battle.Game = games.CreateArenaGame(pending.GameId, pending.SessionState.SessionName, pending.SessionState.EncounterData,
				battle.Human, deck, userName, selfStops, opponentStops, battle.Ai, pending.AiDeck,
				new List<EncounterModBase>(),   // buffs and Hogarth's challenges come in build step 6
				(engine, winners, losers) => OnGameEnded(profileId, userName, battle, engine, winners));
		}
		catch (Exception ex)
		{
			_active.TryRemove(profileId, out _);
			return Fail("the battle could not start: " + ex.Message, out reason);
		}

		reason = string.Empty;
		return true;
	}

	public bool TryGetActive(SessionContext context, out ActiveBattle battle) => _active.TryGetValue(context.ProfileId, out battle!);

	/// <summary>Engine thread, before the final flush sends the game's end to the player: the next JoinCampaignArena
	/// (when the player returns to the lobby) already sees the new run state.</summary>
	private void OnGameEnded(ulong profileId, string userName, ActiveBattle battle, HexRulesEngine engine, List<UID> winners)
	{
		try
		{
			if (Interlocked.Exchange(ref battle.Applied, 1) == 1)
				return;
			_active.TryRemove(new KeyValuePair<ulong, ActiveBattle>(profileId, battle));
			var ai = engine.AiSeat?.Summary() ?? "no AI seat";

			if (engine.IsVoided || winners.Count == 0)
			{
				_logger?.LogWarning("Arena: battle for {user} voided ({reason}); the fight will be replayed. AI: {ai}",
					userName, engine.VoidReason ?? "no winner", ai);
				return;
			}

			if (!_store.TryGet(profileId, out var run) || run.ArenaId != battle.ArenaId || run.State != ArenaRunRecord.Active)
			{
				_logger?.LogWarning("Arena: battle for {user} ended but its run is gone; result dropped", userName);
				return;
			}

			var fight = run.Fights.FirstOrDefault(f => f.FightId == battle.FightId);
			if (fight is null || fight.Result != "NONE")
			{
				_logger?.LogWarning("Arena: battle for {user} ended but fight {fight} is already decided; result dropped", userName, battle.FightId);
				return;
			}

			var won = winners.Contains(battle.Human);
			var opponent = ArenaRoster.Find(fight.ChallengerId)?.ChampionName ?? fight.ChallengerId.ToString();
			if (won)
			{
				fight.Result = "WIN";
				run.Wins++;
			}
			else
			{
				run.Loses++;
				run.LastTierLoss |= 2 << (fight.Order / 5);   // TierLoss: tier 1 = 2, tier 2 = 4, tier 3 = 8, tier 4 = 16
				if (fight.Order % 5 != 4)
					fight.Result = "LOSE";   // D5-A: a lost regular fight is a strike and the run moves on; a lost boss is replayed
			}
			_store.Save(run);

			_logger?.LogInformation("Arena: {user} {outcome} fight {order} against {opponent} in {turns} turns; run now {wins} wins, {loses} strikes. AI: {ai}",
				userName, won ? "won" : "lost", fight.Order, opponent, engine.m_TotalTurnsTaken, run.Wins, run.Loses, ai);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Arena: applying the battle result failed for {user}", userName);
		}
	}

	private static bool Fail(string why, out string reason)
	{
		reason = why;
		return false;
	}
}
