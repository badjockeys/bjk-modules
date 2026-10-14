using BJK.Modules.Shared;
using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using MediatR;

namespace BJK.Modules.User.UseCases.List;

/// <summary>Lists active users, newest first. Requires the manage scope (enforced on the endpoint).</summary>
public sealed record ListUsersQuery(int Page = 1, int PageSize = 20) : IRequest<PagedResult<UserDto>>;

public sealed class ListUsersQueryValidator : AbstractValidator<ListUsersQuery>
{
    public const int MaxPageSize = 100;

    public ListUsersQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, MaxPageSize);
    }
}

public sealed class ListUsersQueryHandler(IUserRepository users)
    : IRequestHandler<ListUsersQuery, PagedResult<UserDto>>
{
    public async Task<PagedResult<UserDto>> Handle(ListUsersQuery request, CancellationToken cancellationToken)
    {
        var (items, total) = await users.ListAsync(request.Page, request.PageSize, cancellationToken);
        return new PagedResult<UserDto>(items.Select(UserDto.From).ToList(), request.Page, request.PageSize, total);
    }
}
