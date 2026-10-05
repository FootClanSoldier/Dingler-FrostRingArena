extern alias HexGame;
using HexGame::Game.Shared;
using HexGame::Game.Shared.AI;
using HexGame::Game.Shared.Mechanics.GameActions;
using Dingler.Game.GameObjects;
using Dingler.Game.Games;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Arena.Ai;

/// <summary>
/// Hosts the client's own AI as the arena opponent (the type-248 seat) inside the engine. The engine hands this seat
/// its events instead of sending them over the network. At each pump step (FlushReady, on the engine's thread) the
/// queued events are delivered to the AI behind a live rebuild of the board as the AI sees it; the AI answers
/// synchronously with transactions. A stalled, crashed or looping AI voids the battle (design 03 D15-A).
/// </summary>
internal sealed class ArenaAiSeat : IDisposable
{
	private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);
	private const int MaxResyncs = 3;
	private const int LivelockMoves = 5000;

	private readonly HexRulesEngine _engine;
	private readonly ILogger? _logger;
	private readonly object _gate = new();
	private readonly List<(int Class, byte[] Data)> _queue = new();
	private readonly Dictionary<string, byte[]> _lastSent = new();
	private readonly Timer _wake;
	private bool _triggerQueued;
	private DateTime? _awaitingSince;   // the AI was asked to decide and hasn't moved yet (pump thread)
	private volatile bool _awaiting;     // the same, for the wake timer's thread
	private int _resyncs;
	private string? _progressKey;
	private int _progressAt;

	public readonly Player Player;
	public readonly ArenaAiMirror Mirror;
	public readonly HashSet<string> RevealNow = new();
	public int SyncErrors, OptionErrors, IndexPatches, AutoDiscards, RoutingErrors, Deliveries;

	public ArenaAiSeat(HexRulesEngine engine, TrackedPlayer ai, TrackedPlayer human, ILogger? logger)
	{
		_engine = engine;
		_logger = logger;
		Player = ai;

		var state = new SessionState
		{
			SessionId = engine.m_SessionId, SessionName = "AILocalSession_" + ai.m_PlayerId,
			MinimumPlayerCount = 2, MaximumPlayerCount = 2, EncounterData = engine.m_EncounterData,
		};
		Mirror = new ArenaAiMirror(state, engine);
		var self = new AIPlayer(new PlayerState { PlayerId = ai.m_PlayerId, PlayerPosition = ai.m_PlayerPosition });
		Mirror.AddPlayer(self);
		Mirror.AddPlayer(new RemotePlayer(new PlayerState { PlayerId = human.m_PlayerId, PlayerPosition = human.m_PlayerPosition }, UID.Invalid));
		self.InitializeAITactical(new AIPersonality());   // the client's default (Hard, 11): design 03 D9-A
		Mirror.SelfPlayer = self;

		// While the AI owes a move, wake the idle pump every 2 s so a stall is noticed. Never otherwise: each wake makes
		// the engine resend the priority player's GreenLight and options, which the human client would also get.
		_wake = new Timer(_ => { if (_awaiting && !_engine.IsGameEnded) _engine.ProcessWork(); }, null, 2000, 2000);
	}

	/// <summary>Called from the engine's dispatch (any thread before the game starts, the pump thread after).</summary>
	public void Enqueue(SessionEventArgs args)
	{
		// Card, player and combat state is rebuilt live instead (BuildAiSync).
		if (args is CardUpdatedSessionEventArgs or PlayerUpdatedSessionEventArgs or CombatListingSessionEventArgs)
			return;
		var data = args.ToByteArray();   // serialized now: option lists point into engine-owned lists
		lock (_gate)
		{
			_queue.Add((args.Class, data));
			if (IsDecisionFor(args))
				_triggerQueued = true;
		}
	}

	public void NoteReveal(IEnumerable<SessionCardId> cards)
	{
		foreach (var id in cards)
			RevealNow.Add(id.ToString());
	}

	public void AddIfChanged(string key, SessionEventArgs e, List<SessionEventArgs> into)
	{
		byte[] bytes;
		try { bytes = e.ToByteArray(); }
		catch { into.Add(e); return; }
		if (_lastSent.TryGetValue(key, out var old) && old.AsSpan().SequenceEqual(bytes))
			return;
		_lastSent[key] = bytes;
		into.Add(e);
	}

	/// <summary>Forget what the AI was sent, so the next sync resends its whole view.</summary>
	public void ResetSync() => _lastSent.Clear();

	/// <summary>Engine pump thread, after each step.</summary>
	public void OnFlush()
	{
		try
		{
			if (_engine.IsGameEnded || _engine.IsVoidRequested)
				return;

			List<(int Class, byte[] Data)> batch;
			bool trigger;
			lock (_gate)
			{
				batch = _queue.ToList();
				_queue.Clear();
				trigger = _triggerQueued;
				_triggerQueued = false;
			}

			if (batch.Count == 0)
			{
				CheckStall();
				return;
			}
			if (trigger && _awaitingSince is null)
				SetAwaiting(DateTime.UtcNow);

			// The AI reads its own hand and champion from the player object; the mirror has no Card objects.
			Mirror.SelfPlayer!.m_ChampionCard = Player.m_ChampionCard;
			Mirror.SelfPlayer.m_Hand = Player.m_Hand;

			var decoded = batch.Select(b => SessionEventArgs.BuildArgs(b.Class, b.Data)).ToList();
			// One decision per delivery: if several GreenLights for the AI queued up (e.g. the game's start plus the pump's
			// first step), keep only the last, or the AI acts twice on the same state.
			var lastGreenLight = decoded.FindLastIndex(e => e is GreenLightSessionEventArgs g && g.PlayerId.Equals(Player.m_PlayerId));
			if (lastGreenLight >= 0)
				decoded = decoded.Where((e, i) => i == lastGreenLight || !(e is GreenLightSessionEventArgs g && g.PlayerId.Equals(Player.m_PlayerId))).ToList();
			var events = new List<SessionEventArgs>();
			events.AddRange(decoded.Where(e => e is GameStartedSessionEventArgs));
			events.AddRange(_engine.BuildAiSync(this));
			events.AddRange(decoded.Where(e => e is not GameStartedSessionEventArgs));

			var before = Mirror.SubmittedTotal;
			Deliveries++;
			foreach (var e in events)
			{
				try
				{
					Mirror.Route(e);
				}
				catch (Exception ex)
				{
					RoutingErrors++;
					_logger?.LogWarning("Arena AI: routing {Event} threw {Error}", e.GetType().Name, ex.Message);
					// These two handlers have no safety net in the AI: the engine's request stays unanswered forever.
					if (e is AbilityActivationDataRequiredSessionEventArgs or TriggeredAbilityActivationDataRequiredSessionEventArgs)
					{
						_engine.RequestVoid($"AI error answering {e.GetType().Name}: {ex.Message}");
						return;
					}
				}
			}

			if (Mirror.SubmittedTotal != before)
			{
				SetAwaiting(null);
				_resyncs = 0;
			}

			CheckLivelock();
			CheckStall();   // on every step: an AI that never moves still gets GreenLights resent, so the queue is rarely empty
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Arena AI host failed");
			_engine.RequestVoid("AI host exception: " + ex.Message);
		}
	}

	private bool IsDecisionFor(SessionEventArgs e) => e switch
	{
		GreenLightSessionEventArgs g => g.PlayerId.Equals(Player.m_PlayerId),
		AbilityActivationDataRequiredSessionEventArgs a => a.PlayerId.Equals(Player.m_PlayerId),
		TriggeredAbilityActivationDataRequiredSessionEventArgs t => t.PlayerId.Equals(Player.m_PlayerId),
		CombatsThatNeedDamageSessionEventArgs c => c.PlayerId.Equals(Player.m_PlayerId),
		_ => false,
	};

	// The AI was asked to decide and hasn't moved. Inside a priority window a resync may help (it resends the
	// GreenLight); anywhere else the engine waits for input that will never come.
	private void CheckStall()
	{
		if (_awaitingSince is not { } since || DateTime.UtcNow - since < StallTimeout)
			return;

		var action = _engine.CurrentGameAction;
		if (action is PriorityWindowAction && _resyncs < MaxResyncs)
		{
			_resyncs++;
			SetAwaiting(DateTime.UtcNow);
			_logger?.LogWarning("Arena AI: no move for {Seconds}s; asking for a priority resync ({Count}/{Max})", StallTimeout.TotalSeconds, _resyncs, MaxResyncs);
			Mirror.SelfPlayer!.RequestPriorityResync();
			return;
		}

		_engine.RequestVoid($"AI stalled; the engine waits in {action?.GetType().Name ?? "nothing"} ({action})");
	}

	// Thousands of AI moves without the turn or phase changing is a loop, not a game.
	private void CheckLivelock()
	{
		var key = _engine.m_TotalTurnsTaken + "/" + _engine.CurrentTurnPhase;
		if (key != _progressKey)
		{
			_progressKey = key;
			_progressAt = Mirror.SubmittedTotal;
		}
		else if (Mirror.SubmittedTotal - _progressAt > LivelockMoves)
		{
			_engine.RequestVoid($"AI livelock in {_engine.CurrentTurnPhase}");
		}
	}

	private void SetAwaiting(DateTime? since)
	{
		_awaitingSince = since;
		_awaiting = since is not null;
	}

	public string Summary() =>
		$"deliveries {Deliveries}, moves {Mirror.SubmittedTotal}, livelock guard {Mirror.Suppressions}, choose-one fixes {IndexPatches}, " +
		$"auto-discards {AutoDiscards}, routing errors {RoutingErrors}, sync errors {SyncErrors}, option errors {OptionErrors}";

	public void Dispose() => _wake.Dispose();
}
