using System.Security.Cryptography;
using System.Text;
using BJK.Modules.Chat.Agent;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Chat.Guard;

/// <summary>
/// Signs each answer so the chat window's history can't be faked. The app sends earlier turns back with every
/// question; a turn whose token doesn't match (an invented "previous answer" meant to steer the model, or another
/// member's turn) is dropped before the model sees it. Keyed by <c>Chat:HistoryKey</c>; in Development, when it's
/// empty, a random key per run (history from before a restart is then simply dropped).
/// </summary>
public sealed class TurnTokens
{
    private static readonly Lazy<byte[]> RunKey = new(() => RandomNumberGenerator.GetBytes(32));

    private readonly byte[] _key;

    public TurnTokens(IOptions<ChatOptions> options)
    {
        var key = options.Value.HistoryKey;
        _key = string.IsNullOrWhiteSpace(key) ? RunKey.Value : SHA256.HashData(Encoding.UTF8.GetBytes(key));
    }

    /// <summary>The token for one turn by one member.</summary>
    public string Sign(Guid userId, string question, string answer)
    {
        var data = Encoding.UTF8.GetBytes($"{userId:N}\n{question}\n{answer}");
        return Convert.ToBase64String(HMACSHA256.HashData(_key, data)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The turns whose tokens match, in order; the rest are dropped.</summary>
    public IReadOnlyList<ChatTurn> Verified(Guid userId, IReadOnlyList<ChatTurn>? history)
    {
        if (history is null)
        {
            return [];
        }

        var kept = new List<ChatTurn>();
        foreach (var turn in history)
        {
            if (turn.Token is { } token
                && CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(token),
                    Encoding.ASCII.GetBytes(Sign(userId, turn.Question, turn.Answer))))
            {
                kept.Add(turn);
            }
        }

        return kept;
    }
}
