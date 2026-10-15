using Microsoft.AspNetCore.SignalR;
using QuizLive.Contracts;
using QuizLive.Services;

namespace QuizLive.Hubs;

/// <summary>
/// Transport only. Every rule lives in <see cref="GameService"/>; this class
/// decides who receives what. Each room is a SignalR group named after its
/// code, so a broadcast reaches exactly the people in that game.
/// </summary>
public class QuizHub : Hub
{
    private readonly GameService _games;

    public QuizHub(GameService games)
    {
        _games = games;
    }

    private static string Group(string code) => $"room:{code.ToUpperInvariant()}";

    /// <summary>Host opens the room and starts receiving its broadcasts.</summary>
    public async Task<StateSnapshot?> HostJoin(string code)
    {
        code = code.ToUpperInvariant();
        if (!await _games.AttachHostAsync(code, Context.ConnectionId))
            return null;

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(code));
        return await _games.SnapshotAsync(code, null);
    }

    /// <summary>
    /// Player joins, or reclaims their seat with a token from a previous
    /// connection. The returned snapshot is what lets a reloaded phone render
    /// the live question with the correct time remaining.
    /// </summary>
    public async Task<JoinResult> PlayerJoin(string code, string name, string? reconnectToken)
    {
        code = code.ToUpperInvariant();
        var result = await _games.JoinAsync(code, name, reconnectToken, Context.ConnectionId);
        if (!result.Ok) return result;

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(code));
        await BroadcastLobbyAsync(code);
        return result;
    }

    public async Task<QuestionView?> NextQuestion(string code)
    {
        code = code.ToUpperInvariant();
        var question = await _games.NextQuestionAsync(code);

        if (question is null)
        {
            var final = await _games.SnapshotAsync(code, null);
            await Clients.Group(Group(code)).SendAsync("GameOver", final);
            return null;
        }

        await Clients.Group(Group(code)).SendAsync("QuestionStarted", question);
        return question;
    }

    /// <summary>
    /// Takes only the chosen option. There is deliberately no timing parameter
    /// here: the server times the answer itself.
    /// </summary>
    public async Task<AnswerResult> SubmitAnswer(string code, int playerId, int optionIndex)
    {
        code = code.ToUpperInvariant();
        var result = await _games.SubmitAnswerAsync(code, playerId, optionIndex);

        // Let the host's "answers in" counter move, but only when this call
        // actually recorded something new.
        if (result.Accepted && !result.WasDuplicate)
        {
            var snapshot = await _games.SnapshotAsync(code, null);
            if (snapshot is not null)
                await Clients.Group(Group(code)).SendAsync("AnswersProgress", snapshot.AnswersIn, snapshot.PlayerCount);
        }

        return result;
    }

    public async Task<RevealView?> Reveal(string code)
    {
        code = code.ToUpperInvariant();
        var reveal = await _games.RevealAsync(code);
        if (reveal is not null)
            await Clients.Group(Group(code)).SendAsync("Revealed", reveal);

        return reveal;
    }

    /// <summary>Re-fetch everything after a dropped connection.</summary>
    public Task<StateSnapshot?> Resync(string code, int? playerId) =>
        _games.SnapshotAsync(code.ToUpperInvariant(), playerId);

    private async Task BroadcastLobbyAsync(string code)
    {
        var snapshot = await _games.SnapshotAsync(code, null);
        if (snapshot is not null)
            await Clients.Group(Group(code)).SendAsync("LobbyUpdated", snapshot.Leaderboard, snapshot.PlayerCount);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _games.MarkDisconnectedAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
