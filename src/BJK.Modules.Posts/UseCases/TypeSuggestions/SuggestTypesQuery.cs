using BJK.Modules.Media.Contracts;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.TypeSuggestions;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Posts.UseCases.TypeSuggestions;

/// <summary>
/// "Looks like: Tailgating": suggested incident types for the caller's photos or video, from the CLIP summaries the
/// media worker made while blurring them. Only media that are ready count; with none, there are no suggestions. The
/// composer shows them as chips the person taps to add; nothing is ticked for them.
/// </summary>
/// <param name="MediaIds">The post's photos or video.</param>
public sealed record SuggestTypesQuery(IReadOnlyList<Guid> MediaIds) : IRequest<TypeSuggestionsDto>;

/// <summary>A suggested type and how strongly the pictures fit it (0 to 1).</summary>
public sealed record TypeSuggestionDto(IncidentCategory Type, double Score);

/// <summary>Suggestions, best first (often none).</summary>
public sealed record TypeSuggestionsDto(IReadOnlyList<TypeSuggestionDto> Suggestions);

public sealed class SuggestTypesQueryValidator : AbstractValidator<SuggestTypesQuery>
{
    public SuggestTypesQueryValidator()
    {
        RuleFor(q => q.MediaIds)
            .NotEmpty()
            .Must(ids => ids is null || ids.Count <= Post.MaxMedia)
            .WithMessage($"A post can have up to {Post.MaxMedia} photos or videos.");
    }
}

public sealed class SuggestTypesQueryHandler(
    IMediaDirectory media,
    ICurrentUser caller,
    IOptions<TypeSuggestionOptions> options)
    : IRequestHandler<SuggestTypesQuery, TypeSuggestionsDto>
{
    public async Task<TypeSuggestionsDto> Handle(SuggestTypesQuery request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(caller.IdentityId, out var userId))
        {
            throw new ForbiddenException("This needs a signed-in user's token.");
        }

        var summaries = await media.GetClipSummariesAsync(userId, request.MediaIds.Distinct().ToList(), cancellationToken);
        var summary = ClipEmbedding.Average(summaries.Values.Select(v => (v, 1)));
        if (summary is null)
        {
            return new TypeSuggestionsDto([]);
        }

        var settings = options.Value;
        var suggestions = TypePrompts.Builtin.Value.Score(summary)
            .Where(s => s.Score >= settings.MinScore)
            .Take(settings.MaxSuggestions)
            .Select(s => new TypeSuggestionDto(s.Type, Math.Round(s.Score, 3)))
            .ToList();
        return new TypeSuggestionsDto(suggestions);
    }
}

/// <summary>Section <c>Posts:TypeSuggestions</c>.</summary>
public sealed class TypeSuggestionOptions
{
    public const string SectionName = "Posts:TypeSuggestions";

    /// <summary>Lowest share (0 to 1) worth suggesting. Default 0.3: only types that clearly fit better than ordinary traffic.</summary>
    public double MinScore { get; set; } = 0.3;

    /// <summary>Most suggestions shown. Default 2.</summary>
    public int MaxSuggestions { get; set; } = 2;
}
