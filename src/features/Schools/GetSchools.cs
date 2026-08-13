using Carter;
using domain.Exceptions;
using domain.Schools;
using domain.Security;
using features.Paging;
using FluentValidation;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace features.Schools;

/// <summary>
///     <c>GET /schools</c> — paged, active-only by default, scoped to the caller.
/// </summary>
public static partial class GetSchools
{
    /// <summary>Longest accepted <c>?q</c>, matching the column it searches.</summary>
    public const int MaxSearchTermLength = 200;

    private const string LikeEscapeCharacter = "\\";

    public sealed class Query : IRequest<PagedResponse<GetSchoolById.Response>>
    {
        public int? Page { get; init; }

        public int? PageSize { get; init; }

        /// <summary>Defaults to false. Deactivated schools are hidden from list results only (DEC-19).</summary>
        public bool IncludeInactive { get; init; }

        /// <summary>Case-insensitive substring match on <c>Name</c>. Absent or blank means no filter.</summary>
        public string? Q { get; init; }
    }

    /// <summary>
    ///     Builds the <c>LIKE</c> pattern for <c>?q</c>, or null when there is no filter to apply.
    /// </summary>
    /// <remarks>
    ///     <b>The backslash is escaped first.</b> Escaping <c>%</c> or <c>_</c> before <c>\</c> would
    ///     re-escape the backslashes the earlier pass introduced, turning <c>\%</c> into two literal
    ///     backslashes followed by a live wildcard. Order is the bug, so it has its own test.
    ///     <para>
    ///         Without escaping, <c>?q=%</c> returns everything and <c>?q=_</c> matches any single
    ///         character — a filter the client did not ask for.
    ///     </para>
    /// </remarks>
    internal static string? ToLikePattern(string? searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return null;

        string escaped = searchTerm
            .Replace(LikeEscapeCharacter, LikeEscapeCharacter + LikeEscapeCharacter, StringComparison.Ordinal)
            .Replace("%", LikeEscapeCharacter + "%", StringComparison.Ordinal)
            .Replace("_", LikeEscapeCharacter + "_", StringComparison.Ordinal);

        return $"%{escaped}%";
    }

    internal sealed class QueryValidator : AbstractValidator<Query>
    {
        public QueryValidator()
        {
            RuleFor(query => query.Page).ValidPage();
            RuleFor(query => query.PageSize).ValidPageSize();

            RuleFor(query => query.Q)
                .MaximumLength(MaxSearchTermLength)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant(
                    $"Search term must not exceed {MaxSearchTermLength} characters."));
        }
    }

    internal sealed class QueryHandler(IDbContext dbContext, ICurrentUser currentUser)
        : IRequestHandler<Query, PagedResponse<GetSchoolById.Response>>
    {
        public async Task<PagedResponse<GetSchoolById.Response>> Handle(
            Query request,
            CancellationToken cancellationToken)
        {
            IQueryable<School> schools = dbContext.Schools.AsNoTracking();

            // School's scope key is its own Id, not a SchoolId, so WhereAuthorized — which is
            // constrained to ISchoolScoped — does not apply. The admin short-circuit is copied from it
            // and is load-bearing for the same reason: an empty scope must not mean "no rows" for an
            // administrator.
            if (!currentUser.IsSystemAdmin)
                schools = schools.Where(school => currentUser.AuthorizedSchoolIds.Contains(school.Id));

            // By composition, never `school.IsActive || @includeInactive`: a disjunction against a
            // parameter defeats any index on the column.
            if (!request.IncludeInactive)
                schools = schools.Where(school => school.IsActive);

            string? pattern = ToLikePattern(request.Q);

            if (pattern is not null)
            {
                string loweredPattern = pattern.ToLowerInvariant();

                // ToLower() is a translation marker, not a .NET call: it becomes SQL lower() and is
                // never executed in process, so the current culture cannot reach it. The invariant
                // overloads the analyzers want have no relational translation, which is why the
                // suppression is here rather than a different call.
#pragma warning disable CA1304, CA1311
                schools = schools.Where(school =>
                    EF.Functions.Like(school.Name.ToLower(), loweredPattern, LikeEscapeCharacter));
#pragma warning restore CA1304, CA1311
            }

            // Total order. CreatedAt or Name alone is not unique, and a non-total order under the
            // global SplitQuery setting can repeat a row on one page and drop another (VC-27).
            return await schools
                .OrderBy(school => school.Name)
                .ThenBy(school => school.Id)
                .Select(GetSchoolById.Projection)
                .ToPagedResponseAsync(request.Page, request.PageSize, cancellationToken);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapGet("/schools", async (
                    int? page,
                    int? pageSize,
                    bool? includeInactive,
                    string? q,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    PagedResponse<GetSchoolById.Response> schools = await mediator.Send(
                        new Query
                        {
                            Page = page,
                            PageSize = pageSize,
                            IncludeInactive = includeInactive ?? false,
                            Q = q
                        },
                        cancellationToken);

                    return Results.Ok(schools);
                })
                .WithName(nameof(GetSchools))
                .WithTags("Schools")
                .Produces<PagedResponse<GetSchoolById.Response>>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest);
        }
    }
}
