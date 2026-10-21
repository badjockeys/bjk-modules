using System.Text.Json;
using BJK.Modules.Media.Contracts;
using BJK.Modules.Posts.Domain;

namespace BJK.Modules.Posts.Infrastructure.TypeSuggestions;

/// <summary>
/// Zero-shot type suggestions with CLIP: a post's pictures (the average CLIP summary of its media) compared with
/// written descriptions of each incident type and of ordinary scenes (<c>type-prompts.json</c>, made by
/// <c>scripts/ml/clip_text_embeddings.py</c> from <c>scripts/ml/type-prompts.source.json</c> with the same CLIP model
/// the media worker uses). A softmax over every description gives each type a share; a type is only suggested when it
/// fits clearly better than "nothing happening". Rough by nature: the person always decides, and our own trained model
/// replaces this once it does better on the accept rate.
/// </summary>
internal sealed class TypePrompts
{
    public static readonly Lazy<TypePrompts> Builtin = new(Load);

    private readonly float _logitScale;
    private readonly (IncidentCategory? Type, float[] Embedding)[] _prompts;

    private TypePrompts(float logitScale, (IncidentCategory? Type, float[] Embedding)[] prompts)
    {
        _logitScale = logitScale;
        _prompts = prompts;
    }

    /// <summary>Each type's share (0 to 1) for a picture summary, highest first. The baseline isn't a type.</summary>
    public IReadOnlyList<(IncidentCategory Type, double Score)> Score(float[] summary)
    {
        var logits = _prompts.Select(p => _logitScale * ClipEmbedding.Dot(summary, p.Embedding)).ToArray();
        var max = logits.Max();
        var exp = logits.Select(l => Math.Exp(l - max)).ToArray();
        var total = exp.Sum();
        return _prompts
            .Select((p, i) => (p.Type, Share: exp[i] / total))
            .Where(x => x.Type is not null)
            .GroupBy(x => x.Type!.Value)
            .Select(g => (Type: g.Key, Score: g.Sum(x => x.Share)))
            .OrderByDescending(x => x.Score)
            .ToList();
    }

    private static TypePrompts Load()
    {
        using var stream = typeof(TypePrompts).Assembly.GetManifestResourceStream("BJK.Modules.Posts.type-prompts.json")
            ?? throw new InvalidOperationException("The type prompts are missing from the module.");
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        var prompts = root.GetProperty("prompts").EnumerateArray()
            .Select(p =>
            {
                var name = p.GetProperty("type").GetString();
                IncidentCategory? type = Enum.TryParse<IncidentCategory>(name, out var parsed) ? parsed : null;
                var embedding = ClipEmbedding.Normalise(p.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray());
                return (type, embedding);
            })
            .ToArray();
        return new TypePrompts(root.GetProperty("logitScale").GetSingle(), prompts);
    }
}
