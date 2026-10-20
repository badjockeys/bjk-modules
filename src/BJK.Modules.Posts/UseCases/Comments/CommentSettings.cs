using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Comments;

/// <summary>
/// The post's author chooses who can comment: everyone, or nobody (comments off). Comments already there stay, and
/// the author can turn comments back on at any time.
/// </summary>
/// <param name="PostId">The post.</param>
/// <param name="Setting">Who can comment.</param>
public sealed record SetCommentSettingCommand(Guid PostId, CommentSetting Setting) : IRequest;

public sealed class SetCommentSettingCommandValidator : AbstractValidator<SetCommentSettingCommand>
{
    public SetCommentSettingCommandValidator()
    {
        RuleFor(c => c.Setting).IsInEnum();
    }
}

public sealed class SetCommentSettingCommandHandler(IPostRepository posts, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<SetCommentSettingCommand>
{
    public async Task Handle(SetCommentSettingCommand request, CancellationToken cancellationToken)
    {
        var post = await PostAccess.GetForChangeAsync(posts, caller, request.PostId, cancellationToken);
        post.SetCommentSetting(request.Setting, clock.GetUtcNow());
        await posts.SaveChangesAsync(cancellationToken);
    }
}
