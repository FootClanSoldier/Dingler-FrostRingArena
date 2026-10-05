extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using Dingler.Game.Arena;
using HexGame::Game.Client.Network.Campaign;
using HexGame::Game.Shared.Network.Campaign;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Campaign;

// Frost Ring Arena, 10033: sent from the final loot window after a successful cash-out. Deletes a cashed-out run and
// unlocks its deck. An active run is never deleted here. Nothing to delete is fine (the client may send it twice).
[Authenticated]
public sealed class DestroyArenaDataRequestHandler : IRequestHandler<DestroyArenaDataRequestArgs, DestroyArenaDataResponse>
{
	private readonly ArenaRunStore _store;
	private readonly ILogger<DestroyArenaDataRequestHandler>? _logger;

	public DestroyArenaDataRequestHandler(ArenaRunStore store, ILogger<DestroyArenaDataRequestHandler>? logger = null)
	{
		_store = store;
		_logger = logger;
	}

	public DestroyArenaDataResponse HandleRequest(SessionContext context, DestroyArenaDataRequestArgs request)
	{
		try
		{
			if (!_store.TryGet(context.ProfileId, out var run))
				return new DestroyArenaDataResponse { Success = true, Error = EDestroyArenaDataError.Ok };

			if (run.State != ArenaRunRecord.CashedOut)
			{
				_logger?.LogWarning("Arena: {user} asked to destroy active run {arena}; refused", context.UserName, run.ArenaId);
				return new DestroyArenaDataResponse { Success = false, Error = EDestroyArenaDataError.Ok };
			}

			_store.Delete(context.ProfileId);
			ArenaDeckLock.Release(context, run.DeckId);
			_logger?.LogInformation("Arena: {user}'s run {arena} destroyed after cash-out; deck unlocked", context.UserName, run.ArenaId);
			return new DestroyArenaDataResponse { Success = true, Error = EDestroyArenaDataError.Ok };
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Arena: destroying the run failed for {user}", context.UserName);
			return new DestroyArenaDataResponse { Success = false, Error = EDestroyArenaDataError.InternalServerError };
		}
	}
}
