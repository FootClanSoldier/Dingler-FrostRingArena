namespace Dingler.Game.Arena;

/// <summary>
/// Builds a new run's 20 fights by the owner's decisions (design 03 §8):
/// D1-A, the 2018 bracket (docs/04 §1b-§2):
///   tier 1 = 4 of the 6 tier-1 regulars + Eternal Guardian;
///   tiers 2-4 = 4 standard opponents, of which 1/2/2 are their Elite version (random slots), + a normal boss;
///   tier 4's boss = an Elite boss (the final fight). No champion twice: an Elite counts as its normal version.
/// D1b-A, each rare encounter (Uruunaz, Zakiir) is rolled once per run at 1 in 200 and replaces a regular slot of
///   tiers 2-4, never the first fight of a tier.
/// D3-A/D4-A, one of Hogarth's 21 challenges on a random regular fight of each tier.
/// </summary>
public static class ArenaBracketBuilder
{
	public const double RareChancePerRun = 1.0 / 200;           // D1b-A
	private static readonly int[] ElitesInTier = { 0, 1, 2, 2 }; // tiers 1-4 (docs/04 §2)

	public static List<ArenaFightRecord> Build(ulong arenaId, Random rng)
	{
		var slots = new ArenaOpponent[20];

		// Tier 1: four of the tier-1 regulars, then Eternal Guardian.
		var tier1 = Shuffle(ArenaRoster.InGroup(ArenaGroup.Tier1Regular), rng);
		for (var i = 0; i < 4; i++) slots[i] = tier1[i];
		slots[4] = ArenaRoster.InGroup(ArenaGroup.Tier1Boss)[0];

		// Tiers 2-4: twelve different standard opponents; the Elite version in 1/2/2 random slots.
		var standard = Shuffle(ArenaRoster.InGroup(ArenaGroup.Standard), rng);
		var next = 0;
		for (var tier = 2; tier <= 4; tier++)
		{
			var eliteSlots = Shuffle(Enumerable.Range(0, 4).ToList(), rng).Take(ElitesInTier[tier - 1]).ToHashSet();
			for (var p = 0; p < 4; p++)
			{
				var normal = standard[next++];
				slots[(tier - 1) * 5 + p] = eliteSlots.Contains(p) ? ArenaRoster.Twin(normal) ?? normal : normal;
			}
		}

		// Bosses: three different ones; the final boss in its Elite version.
		var bosses = Shuffle(ArenaRoster.InGroup(ArenaGroup.Boss), rng);
		slots[9] = bosses[0];
		slots[14] = bosses[1];
		slots[19] = ArenaRoster.Twin(bosses[2]) ?? bosses[2];

		// Rare encounters: a regular slot of tiers 2-4, not the tier's first fight (orders 6-8, 11-13, 16-18).
		var rareCandidates = Shuffle(new List<int> { 6, 7, 8, 11, 12, 13, 16, 17, 18 }, rng);
		var rareSlots = new HashSet<int>();
		foreach (var rare in ArenaRoster.InGroup(ArenaGroup.Rare))
		{
			if (rng.NextDouble() >= RareChancePerRun) continue;
			var slot = rareCandidates.First(s => !rareSlots.Contains(s));
			rareSlots.Add(slot);
			slots[slot] = rare;
		}

		// Hogarth's challenges: one per tier, on a regular fight that isn't a rare encounter.
		var challenges = ArenaRoster.Challenges;
		var challengeAt = new Dictionary<int, string>();
		for (var tier = 1; tier <= 4 && challenges.Count > 0; tier++)
		{
			var candidates = Enumerable.Range((tier - 1) * 5, 4).Where(o => !rareSlots.Contains(o)).ToList();
			challengeAt[candidates[rng.Next(candidates.Count)]] = challenges[rng.Next(challenges.Count)].ToString();
		}

		return Enumerable.Range(0, 20).Select(order => new ArenaFightRecord
		{
			FightId = arenaId * 32 + (ulong)order + 1,
			Order = order,
			ChallengerId = slots[order].Challenger.ChallengerID,
			Challenge = challengeAt.GetValueOrDefault(order),
		}).ToList();
	}

	private static List<T> Shuffle<T>(IReadOnlyList<T> items, Random rng)
	{
		var list = items.ToList();
		for (var i = list.Count - 1; i > 0; i--)
		{
			var j = rng.Next(i + 1);
			(list[i], list[j]) = (list[j], list[i]);
		}
		return list;
	}
}
