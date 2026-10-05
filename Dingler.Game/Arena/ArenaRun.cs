extern alias HexGame;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Mechanics;
using HexGame::Reckoning.Campaign.Messages.Arena;

namespace Dingler.Game.Arena;

/// <summary>One player's arena run as the server stores it (design 03 §3.3). The client's own objects (ArenaData,
/// ArenaFight, ...) are rebuilt from this on every reply; the client's copies are never trusted.</summary>
public sealed class ArenaRunRecord
{
	public const string Active = "Active";
	public const string CashedOut = "CashedOut";

	public int Version { get; set; } = 1;
	public ulong ArenaId { get; set; }
	public ulong ProfileId { get; set; }
	public ulong DeckId { get; set; }
	public int Mode { get; set; } = (int)ECampaignDifficulty.NORMAL;
	public int Wins { get; set; }
	public int Loses { get; set; }
	public int LastTierLoss { get; set; }
	public bool IsBuyout { get; set; }
	public string State { get; set; } = Active;
	public DateTime CreatedUtc { get; set; }
	public List<ArenaFightRecord> Fights { get; set; } = new();
	/// <summary>Pending buffs (kinds: Health, Charge, Resource, Brawler), max 2, used up when a boss is beaten.</summary>
	public List<string> Buffs { get; set; } = new();
	/// <summary>Hogarth's lines to play when the player is next in the lobby (buff rewards, strike removals).</summary>
	public List<string> PendingConversations { get; set; } = new();
}

public sealed class ArenaFightRecord
{
	public ulong FightId { get; set; }
	public int Order { get; set; }              // 0..19; tier = Order / 5 + 1; Order % 5 == 4 is the boss
	public ulong ChallengerId { get; set; }     // ArenaCatalog challenger id (1..109)
	public string Result { get; set; } = "NONE";
	public string? Challenge { get; set; }      // Hogarth challenge conversation id, or null
	public string ChallengeResponse { get; set; } = "NONE";
}

/// <summary>Builds the client's message objects from a stored run, following the client's rules: exactly 20 fights in
/// FightOrder; the current fight is the "NONE" slot whose FightID equals EncounterData.FightID; every object non-null;
/// ChallengerData never null while the run is active (or the client cashes out by itself on every visit).</summary>
public static class ArenaReplies
{
	public static ArenaFightRecord CurrentFight(ArenaRunRecord run) =>
		run.Fights.OrderBy(f => f.Order).FirstOrDefault(f => f.Result == "NONE") ?? run.Fights.OrderBy(f => f.Order).Last();

	public static ArenaData ToArenaData(ArenaRunRecord run) => new()
	{
		ArenaID = run.ArenaId,
		PlayerID = run.ProfileId,
		GameMode = (ECampaignDifficulty)run.Mode,
		Wins = run.Wins,
		Loses = run.Loses,
		DeckId = run.DeckId,
		FightId = CurrentFight(run).FightId,
		LastTierLoss = run.LastTierLoss,
		IsBuyout = run.IsBuyout,
		Buffs = run.Buffs.Select(ArenaChallengeMods.FindBuff).OfType<ArenaChallengeMods.Buff>().Take(2)
			.Select(ArenaChallengeMods.ToArenaBuff).ToList(),
	};

	public static ArenaFight ToFight(ArenaRunRecord run, ArenaFightRecord f) => new()
	{
		FightID = f.FightId,
		FightTier = f.Order / 5 + 1,
		FightOrder = f.Order,
		ArenaInstance = run.ArenaId,
		ChallengerInstance = f.ChallengerId,
		FightResults = f.Result,
		RoundChallenge = f.Challenge is null ? ResourceId.Invalid : new ResourceId(f.Challenge),
		ChallengeResponse = f.ChallengeResponse,
	};

	public static List<ArenaFight> ToHistory(ArenaRunRecord run) => run.Fights.OrderBy(f => f.Order).Select(f => ToFight(run, f)).ToList();

	public static ArenaChallenger ChallengerOf(ArenaFightRecord f) =>
		ArenaRoster.Find(f.ChallengerId)?.Challenger ?? EmptyChallenger();

	// On a rejoin the client rebuilds the objective panel from Header and Body, so they carry the conversation's own
	// heading and objective text (typos included, as stored).
	public static ArenaMCChallenge MCChallengeOf(ArenaFightRecord f)
	{
		if (f.Challenge is null)
			return EmptyMCChallenge();
		var (heading, objective) = ArenaChallengeMods.ObjectiveOf(f.Challenge);
		return new ArenaMCChallenge { ChallengeID = f.FightId, TemplateID = new ResourceId(f.Challenge), Header = heading, Body = objective };
	}

	// Non-null placeholders for replies without a run (the client dereferences some of them anyway).
	public static ArenaData EmptyArenaData() => new() { GameMode = ECampaignDifficulty.NORMAL, Buffs = new List<ArenaBuff>() };
	public static ArenaFight EmptyFight() => new() { FightResults = "NONE", RoundChallenge = ResourceId.Invalid, ChallengeResponse = "NONE" };
	public static ArenaChallenger EmptyChallenger() => new() { ChallengerName = string.Empty, IsBoss = "false", Equipment = new List<ResourceId>() };
	public static ArenaMCChallenge EmptyMCChallenge() => new() { TemplateID = ResourceId.Invalid, Header = string.Empty, Body = string.Empty };
}
