extern alias HexGame;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Mechanics;

namespace Dingler.Game.Campaign;

/// <summary>
/// Optional test-only champion used to expose the normal PvE UI path while Dingler's
/// real AddChampion/profile persistence is still unimplemented.
/// </summary>
public static class CampaignBootstrapChampion
{
    public static ulong ChampionId(ulong profileId, int race)
    {
        // Stable raw champion_bits.Id. The low-level UID wrapping is performed by the client.
        return unchecked(profileId * 1000UL + (ulong)Math.Clamp(race, 1, 8));
    }

    public static champion_bits Create(ulong profileId, CampaignOptions options, ulong lastCampaignId = 0)
    {
        var race = Math.Clamp(options.DefaultRace, 1, 8);
        return new champion_bits
        {
            Name = options.BootstrapChampionName,
            Id = ChampionId(profileId, race),
            Level = 1,
            CurrentXP = 0,
            ChampionClass = (EChampionClass)options.BootstrapChampionClass,
            Race = (ERace)race,
            Gender = (EGender)options.BootstrapChampionGender,
            OwnerChampionId = 0,
            LastCampaignID = lastCampaignId,
            LastDeckID = 0,
            PetName = string.Empty,
        };
    }
}
