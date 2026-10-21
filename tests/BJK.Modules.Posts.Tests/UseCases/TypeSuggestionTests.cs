using System.Text.Json;
using BJK.Modules.Media.Contracts;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.TypeSuggestions;
using BJK.Modules.Posts.UseCases.TypeSuggestions;
using BJK.Modules.Shared.Security;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BJK.Modules.Posts.Tests.UseCases;

public sealed class TypeSuggestionTests
{
    private static readonly Guid User = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_picture_like_a_types_description_suggests_that_type()
    {
        // A picture embedding equal to one of the BadParking descriptions fits BadParking best.
        var badParking = Prompt("BadParking");

        var scores = TypePrompts.Builtin.Value.Score(badParking);

        Assert.Equal(IncidentCategory.BadParking, scores[0].Type);
        Assert.True(scores[0].Score > 0.5);
        Assert.InRange(scores.Sum(s => s.Score), 0, 1.0001);
    }

    [Fact]
    public void Every_type_but_Other_has_descriptions()
    {
        var types = Prompts().Select(p => p.Type).Distinct().ToList();

        Assert.Contains("baseline", types);
        Assert.All(Enum.GetValues<IncidentCategory>().Where(t => t != IncidentCategory.Other), t => Assert.Contains(t.ToString(), types));
    }

    [Fact]
    public async Task Suggestions_come_from_the_callers_ready_media_and_ordinary_scenes_suggest_nothing()
    {
        var media = Substitute.For<IMediaDirectory>();
        var parking = Guid.NewGuid();
        var road = Guid.NewGuid();
        media.GetClipSummariesAsync(User, Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(parking)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, float[]> { [parking] = Prompt("BadParking") });
        media.GetClipSummariesAsync(User, Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(road)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, float[]> { [road] = Prompt("baseline") });

        var withParking = await Handler(media).Handle(new SuggestTypesQuery([parking]), Ct);
        var ordinary = await Handler(media).Handle(new SuggestTypesQuery([road]), Ct);
        var nothingReady = await Handler(media).Handle(new SuggestTypesQuery([Guid.NewGuid()]), Ct);

        Assert.Equal(IncidentCategory.BadParking, withParking.Suggestions[0].Type);
        Assert.True(withParking.Suggestions.Count <= 2);
        Assert.Empty(ordinary.Suggestions);
        Assert.Empty(nothingReady.Suggestions);
    }

    [Fact]
    public void Suggested_types_are_kept_on_the_post_as_flags()
    {
        var post = Post.Create(
            User,
            new PostDetails("Title", null, IncidentCategory.BadParking, DateTimeOffset.UnixEpoch, 53.4m, -2.8m, "Prescot", [IncidentCategory.BadParking]),
            [Guid.NewGuid()],
            DateTimeOffset.UnixEpoch);

        post.RecordSuggestedTypes([IncidentCategory.BadParking, IncidentCategory.Speeding]);
        Assert.Equal(IncidentTypeFlags.From([IncidentCategory.BadParking, IncidentCategory.Speeding]), post.SuggestedTypeFlags);
        post.RecordSuggestedTypes([]);
        Assert.Null(post.SuggestedTypeFlags);
    }

    private static SuggestTypesQueryHandler Handler(IMediaDirectory media)
    {
        var caller = Substitute.For<ICurrentUser>();
        caller.IdentityId.Returns(User.ToString());
        return new SuggestTypesQueryHandler(media, caller, Options.Create(new TypeSuggestionOptions()));
    }

    private static float[] Prompt(string type) =>
        ClipEmbedding.Normalise(Prompts().First(p => p.Type == type).Embedding);

    private static List<(string Type, float[] Embedding)> Prompts()
    {
        using var stream = typeof(TypePrompts).Assembly.GetManifestResourceStream("BJK.Modules.Posts.type-prompts.json")!;
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.GetProperty("prompts").EnumerateArray()
            .Select(p => (p.GetProperty("type").GetString()!, p.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray()))
            .ToList();
    }
}
