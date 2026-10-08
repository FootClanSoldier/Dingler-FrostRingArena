using System.Text.Json;
using System.Text.Json.Nodes;
using Dingler.Server;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Campaign;

/// <summary>
/// Minimal ServiceCampaign state machine for the first client-visible vertical slice.
/// It deliberately stops before encounter launch/battle integration.
/// </summary>
public sealed class CampaignService
{
    private readonly CampaignRunStore _store;
    private readonly CampaignOptions _options;
    private readonly ILogger<CampaignService>? _logger;

    public CampaignService(CampaignRunStore store, CampaignOptions options, ILogger<CampaignService>? logger = null)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    public byte[] HandleEnvelope(SessionContext context, byte[]? envelope)
    {
        using var document = JsonDocument.Parse(envelope is { Length: > 0 } ? envelope : "{}"u8.ToArray());
        var root = document.RootElement;
        var requestType = String(root, "RequestType");
        _logger?.LogInformation("Campaign: {user} request {request}", context.UserName, requestType);

        JsonNode response = requestType switch
        {
            "qcur4champ" => QueryCurrent(context, root),
            "getactive" => GetActive(context, root),
            "createcamp" => CreateCampaign(context, root),
            "getcampstate" => GetCampaignState(context, root),
            "startcamp" => StartCampaign(context, root),
            "getcampsum" => GetCampaignSummary(context, root),
            "locaction" => LocationAction(context, root),
            "sendevent" => SendEvent(context, root),
            "forfeit" => Forfeit(context, root),
            _ => Unsupported(root, requestType),
        };

        return JsonSerializer.SerializeToUtf8Bytes(response);
    }

    private JsonNode QueryCurrent(SessionContext context, JsonElement root)
    {
        var championId = ULong(root, "ChampID");
        if (championId == 0)
            return CampaignStateFactory.BuildFailure(0, "ChampID is required");
        var record = _store.GetOrCreate(context.ProfileId, championId, _options.DefaultRace);
        return CampaignStateFactory.BuildInputResponse(record);
    }

    private JsonNode CreateCampaign(SessionContext context, JsonElement root)
    {
        // Phase 1 only materializes the starter PANORAMA. The client can still call
        // createcamp; returning the same idempotent record is safer than inventing a dungeon.
        return QueryCurrent(context, root);
    }

    private JsonNode GetActive(SessionContext context, JsonElement root)
    {
        var championId = ULong(root, "ChampID");
        if (championId == 0)
            return new JsonArray();

        var requestedType = Int(root, "CampType", 0);
        if (requestedType is not (0 or 6))
            return new JsonArray();

        var record = _store.GetOrCreate(context.ProfileId, championId, _options.DefaultRace);
        return new JsonArray { CampaignStateFactory.BuildTemplateInfo(record) };
    }

    private JsonNode GetCampaignState(SessionContext context, JsonElement root)
    {
        var campaignId = ULong(root, "CampID");
        return _store.TryGetByCampaignId(context.ProfileId, campaignId, out var record)
            ? CampaignStateFactory.BuildInputResponse(record)
            : CampaignStateFactory.BuildFailure(campaignId, "Campaign not found");
    }

    private JsonNode StartCampaign(SessionContext context, JsonElement root)
    {
        var campaignId = ULong(root, "CampID");
        if (!_store.TryGetByCampaignId(context.ProfileId, campaignId, out var record))
            return CampaignStateFactory.BuildFailure(campaignId, "Campaign not found");

        record.Started = true;
        record.State["Started"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        record.State["CurState"] = "EXPLORE";
        _store.Save(record);
        return CampaignStateFactory.BuildInputResponse(record);
    }

    private JsonNode GetCampaignSummary(SessionContext context, JsonElement root)
    {
        var result = new JsonArray();
        foreach (var campaignId in CampaignIds(root))
        {
            if (_store.TryGetByCampaignId(context.ProfileId, campaignId, out var record))
                result.Add(CampaignStateFactory.BuildCampSummary(record));
        }
        return result;
    }

    private JsonNode LocationAction(SessionContext context, JsonElement root)
    {
        var campaignId = ULong(root, "CampID");
        if (!_store.TryGetByCampaignId(context.ProfileId, campaignId, out var record))
            return CampaignStateFactory.BuildFailure(campaignId, "Campaign not found");

        var action = Int(root, "RAct", 0);
        var location = String(root, "Loc");
        if (action == 0 && !string.IsNullOrWhiteSpace(location))
        {
            record.State["ALoc"] = location;
            record.State["LastNode"] = location;
            record.State["CurState"] = "EXPLORE";
            _store.Save(record);
        }
        return CampaignStateFactory.BuildInputResponse(record);
    }

    private JsonNode SendEvent(SessionContext context, JsonElement root)
    {
        var campaignId = ULong(root, "CampID");
        if (!_store.TryGetByCampaignId(context.ProfileId, campaignId, out var record))
            return CampaignStateFactory.BuildFailure(campaignId, "Campaign not found");

        var eventName = String(root, "Event");
        if (eventName == "conv_done")
            CompleteIntroConversation(record);
        else if (eventName == "enc_cancel")
        {
            record.State["ALoc"] = null;
            record.State["CurState"] = "EXPLORE";
            _store.Save(record);
        }
        else if (eventName == "start")
        {
            // Intentionally no battle hook in phase 1. Keep the campaign alive and visible.
            _logger?.LogInformation("Campaign: phase-1 ignored encounter start for campaign {campaign}", campaignId);
        }
        return CampaignStateFactory.BuildInputResponse(record);
    }

    private void CompleteIntroConversation(CampaignRunRecord record)
    {
        var cfg = CampaignStateFactory.Race(record.Race);
        var active = record.State["ALoc"]?.GetValue<string>();
        if (!string.Equals(active, cfg.IntroNpc, StringComparison.Ordinal))
        {
            record.State["ALoc"] = null;
            record.State["CurState"] = "EXPLORE";
            _store.Save(record);
            return;
        }

        if (record.State["VisLocs"] is JsonArray locations)
        {
            foreach (var item in locations.OfType<JsonObject>())
            {
                var data = item["Data"] as JsonObject;
                if (data?["node"]?.GetValue<string>() == cfg.IntroNpc)
                {
                    data["completed"] = true;
                    data["autostart"] = false;
                }
            }

            var trainerAlreadyPresent = locations
                .OfType<JsonObject>()
                .Any(item => (item["Data"] as JsonObject)?["node"]?.GetValue<string>() == cfg.TrainerNpc);
            if (!trainerAlreadyPresent)
                locations.Add(CampaignStateFactory.ConversationLocation(cfg.TrainerNpc, cfg.BattleConversation, giveQuest: true));
        }

        // Matching the observed client contract: after the intro the player returns to
        // panorama explore mode and must select the trainer rather than auto-launching it.
        record.State["ALoc"] = null;
        record.State["CurState"] = "EXPLORE";
        _store.Save(record);
    }

    private JsonNode Forfeit(SessionContext context, JsonElement root)
    {
        var campaignId = ULong(root, "CampID");
        if (!_store.TryGetByCampaignId(context.ProfileId, campaignId, out var record))
            return CampaignStateFactory.BuildFailure(campaignId, "Campaign not found");
        record.State["Finished"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        record.State["FinishReason"] = "Forfeit";
        record.State["CurState"] = "FINISHED";
        _store.Save(record);
        return CampaignStateFactory.BuildInputResponse(record);
    }

    private static JsonNode Unsupported(JsonElement root, string requestType)
    {
        var campaignId = ULong(root, "CampID");
        return CampaignStateFactory.BuildFailure(campaignId, $"Unsupported campaign request '{requestType}' in phase 1");
    }

    private static IEnumerable<ulong> CampaignIds(JsonElement root)
    {
        if (root.TryGetProperty("CampIDs", out var many) && many.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in many.EnumerateArray())
                if (TryULong(value, out var id)) yield return id;
            yield break;
        }
        var one = ULong(root, "CampID");
        if (one != 0) yield return one;
    }

    private static string String(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return string.Empty;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    private static ulong ULong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return 0;
        return TryULong(value, out var parsed) ? parsed : 0;
    }

    private static bool TryULong(JsonElement value, out ulong parsed)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out parsed)) return true;
        if (value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), out parsed)) return true;
        parsed = 0;
        return false;
    }

    private static int Int(JsonElement root, string name, int fallback)
    {
        if (!root.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)) return parsed;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out parsed)) return parsed;
        return fallback;
    }
}
