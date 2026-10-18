using BJK.Modules.Shared;
using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;
using FluentValidation;
using MediatR;

namespace BJK.Modules.User.UseCases.List;

/// <summary>Lists accounts, newest first, optionally by status. Requires the manage scope (enforced on the endpoint).</summary>
public sealed record ListUsersQuery(UserAccountStatus? Status = null, int Page = 1, int PageSize = 20)
    : IRequest<PagedResult<UserDto>>;

public sealed class ListUsersQueryValidator : AbstractValidator<ListUsersQuery>
{
    public const int MaxPageSize = 100;

    public ListUsersQueryValidator()
    {
        RuleFor(q => q.Status).IsInEnum();
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, MaxPageSize);
    }
}

public sealed class ListUsersQueryHandler(IUserRepository users, IIdentityService identity)
    : IRequestHandler<ListUsersQuery, PagedResult<UserDto>>
{
    public async Task<PagedResult<UserDto>> Handle(ListUsersQuery request, CancellationToken cancellationToken)
    {
        var (items, total) = await users.ListAsync(request.Status, request.Page, request.PageSize, cancellationToken);
        var signIns = await identity.GetAccountsAsync(items.Select(a => a.Id.ToString("D")).ToList(), cancellationToken);

        return new PagedResult<UserDto>(
            items.Select(a => UserDto.From(a, signIns.GetValueOrDefault(a.Id.ToString("D")))).ToList(),
            request.Page,
            request.PageSize,
            total);
    }
}
