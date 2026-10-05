extern alias HexGame;
using System.Collections.Concurrent;
using Dingler.Server;
using Dingler.Game.Games;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Mechanics;
using HexGame::Game.Shared.Network.Campaign;
using HexGame::Game.Shared.Network.Profile;
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
		/// <summary>The player's connection when the battle started; the loot is pushed through it.</summary>
		public required SessionContext Session { get; init; }
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
		var fightId = pending.FightId;
		var fightRecord = run.Fights.FirstOrDefault(f => f.FightId == fightId);
		if (fightRecord is null)
			return Fail("the reserved fight is gone", out reason);
		// The fight's Hogarth challenge and, at a boss, the pending buffs (step 6). Fresh mod objects every battle.
		var battleMods = ArenaChallengeMods.ForFight(run, fightRecord, new Random(), run.PendingConversations.ToList());
		if (run.PendingConversations.Count > 0)
		{
			run.PendingConversations.Clear();   // played at this battle's start
			_store.Save(run);
		}

		var battle = new ActiveBattle
		{
			Human = HumanId(context),
			Ai = new UID(UID.Type.ServiceAI, pending.GameId),
			ArenaId = run.ArenaId,
			FightId = pending.FightId,
			Session = context,
		};
		var profileId = context.ProfileId;
		var userName = context.UserName!;

		// Published before the game exists, so the end-of-game callback always finds (and removes) it.
		_active[profileId] = battle;
		try
		{
			battle.Game = games.CreateArenaGame(pending.GameId, pending.SessionState.SessionName, pending.SessionState.EncounterData,
				battle.Human, deck, userName, selfStops, opponentStops, battle.Ai, pending.AiDeck,
				battleMods,
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
			var isBoss = fight.Order % 5 == 4;
			var tierBit = 2 << (fight.Order / 5);   // TierLoss: tier 1 = 2, tier 2 = 4, tier 3 = 8, tier 4 = 16
			var notes = new List<string>();
			var newLoot = new List<ArenaLootRecord>();
			var earnedFlags = new List<string>();
			if (won)
			{
				fight.Result = "WIN";
				run.Wins++;

				// A won challenge (owner's update, 2026-10-05, from an ex-player's memory): with a strike on record, one strike
				// is removed ("Challenge Win Strike Removal"); with none, one of the 4 buffs at random for the next boss fight
				// (D6-A, at most 2 pending) and its "Reward" line.
				if (fight.Challenge is not null)
				{
					if (run.Loses > 0)
					{
						run.Loses--;
						run.PendingConversations.Add(ArenaChallengeMods.ChallengeWinStrikeRemoval);
						notes.Add("challenge won: strike removed");
					}
					else if (run.Buffs.Count < 2)
					{
						var buff = ArenaChallengeMods.Buffs[Random.Shared.Next(ArenaChallengeMods.Buffs.Count)];
						run.Buffs.Add(buff.Kind);
						run.PendingConversations.Add(buff.RewardConversation);
						notes.Add($"challenge won: {buff.Kind} buff");
					}
				}

				// Loot (step 7, D7b-A). "Perfect tier" = no loss recorded in this tier (its LastTierLoss bit clear).
				newLoot = ArenaLoot.ForWin(fight, isBoss, isBoss && (run.LastTierLoss & tierBit) == 0, ArenaRoster.Find(fight.ChallengerId), Random.Shared);
				run.Loot.AddRange(newLoot);
				earnedFlags = ArenaAccount.FlagsForWin(run, fight);   // step 5: permanent account flags

				if (isBoss)
				{
					if (run.Buffs.Count > 0)
						notes.Add($"buffs used: {string.Join(", ", run.Buffs)}");
					run.Buffs.Clear();   // the buffs were for this boss fight

					// A tier won without a loss in it: one strike removed ("Perfected Tier Strike Removal").
					if ((run.LastTierLoss & tierBit) == 0 && run.Loses > 0 && fight.Order < 19)
					{
						run.Loses--;
						run.PendingConversations.Add(ArenaChallengeMods.PerfectedTierStrikeRemoval);
						notes.Add("strike removed for a perfect tier");
					}
				}
			}
			else
			{
				run.Loses++;
				run.LastTierLoss |= tierBit;
				if (!isBoss)
					fight.Result = "LOSE";   // D5-A: a lost regular fight is a strike and the run moves on; a lost boss is replayed (buffs kept)
			}
			_store.Save(run);
			if (newLoot.Count > 0)
			{
				notes.Add("loot: " + string.Join(", ", newLoot.Select(ArenaLoot.Describe)));
				PushLoot(battle.Session, run, newLoot, userName);
			}
			var newFlags = _store.AddFlags(profileId, earnedFlags);
			if (newFlags.Count > 0)
			{
				notes.Add("new account flags: " + string.Join(", ", newFlags));
				// The client replaces its whole flag list with this one; it shows up at the next lobby open.
				var all = ArenaAccount.ToFlagData(_store.GetFlags(profileId));
				if (!battle.Session.TrySendMessageToClient(new UserFlagsUpdatedEventArgs(all)))
					_logger?.LogWarning("Arena: couldn't push the flags to {user}; they are sent at the next login", userName);
			}
			if (notes.Count > 0)
				_logger?.LogInformation("Arena: {user}: {notes}", userName, string.Join("; ", notes));

			_logger?.LogInformation("Arena: {user} {outcome} fight {order} against {opponent} in {turns} turns; run now {wins} wins, {loses} strikes. AI: {ai}",
				userName, won ? "won" : "lost", fight.Order, opponent, engine.m_TotalTurnsTaken, run.Wins, run.Loses, ai);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Arena: applying the battle result failed for {user}", userName);
		}
	}

	/// <summary>
	/// Sent while the client is still in the battle (the engine reports the end before the final flush). The client only
	/// stores it (ArenaClient.UpdateLoot: no lobby is subscribed) and shows the "Rewards" pop-up when the lobby opens
	/// (SafeOnArenaJoin), without setting m_UINotificationActive, so leaving mid-pop-up can't leave it stuck. Hogarth's
	/// pending lines go right after it: closing the pop-up plays them in order. Sent alone they would wait for a later loot
	/// window, so they go only with loot; if the push fails they stay pending for the next battle's start.
	/// Never an empty list (study 01 §2.12), and one batch per result.
	/// </summary>
	private void PushLoot(SessionContext session, ArenaRunRecord run, List<ArenaLootRecord> loot, string userName)
	{
		var rewards = loot.Select(l => ArenaLoot.ToReward(run, l)).ToList();
		if (!session.TrySendMessageToClient(new LootUpdateEventArgs(rewards)))
		{
			_logger?.LogWarning("Arena: couldn't push the loot to {user} (disconnected?); it is kept for cash-out", userName);
			return;
		}
		if (run.PendingConversations.Count == 0)
			return;
		foreach (var conversation in run.PendingConversations)
			session.TrySendMessageToClient(new BuffConversationEventArgs(new ResourceId(conversation)));
		_logger?.LogInformation("Arena: {user}: {count} Hogarth line(s) sent with the loot", userName, run.PendingConversations.Count);
		run.PendingConversations.Clear();
		_store.Save(run);
	}

	private static bool Fail(string why, out string reason)
	{
		reason = why;
		return false;
	}
}
