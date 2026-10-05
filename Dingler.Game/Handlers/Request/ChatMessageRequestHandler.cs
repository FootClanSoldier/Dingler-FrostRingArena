using Dingler.Game.DeckImport;
using Dingler.Game.Protocol.Chat;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;

namespace Dingler.Game.Handlers.Request;

[Authenticated]
public class ChatMessageRequestHandler : IAsyncRequestHandler<ChatMessageRequest>
{
	private const string ImportCommand = "/importdeck";

	private readonly ChatManager _chatManager;
	private readonly DeckImportService _deckImport;

	public ChatMessageRequestHandler(ChatManager chatManager, DeckImportService deckImport)
	{
		_chatManager = chatManager;
		_deckImport = deckImport;
	}
	
	public async Task HandleRequestAsync(SessionContext context, ChatMessageRequest request, CancellationToken token)
	{
		// Fork: "/importdeck <Hex Codex deck link>" imports a deck for the sender (DeckImportService). It is never
		// broadcast; the answer goes to the sender only, as a chat line in the same room.
		var raw = request.RawChatRequest;
		if (raw.Message.TrimStart().StartsWith(ImportCommand, StringComparison.OrdinalIgnoreCase))
		{
			var result = await _deckImport.ImportLinkAsync(context, raw.Message.TrimStart()[ImportCommand.Length..]);
			context.TrySendMessageToClient(new RawChatRequest { Action = "rchat", Room = raw.Room, User = "Deck import", Message = result.Message });
			return;
		}

		await _chatManager.SendMessageAsync(raw);
	}
}