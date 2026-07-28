using System.Linq.Expressions;
using System.Reflection;
using GarageFlow.Api.Contracts;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Services;

/// <summary>Sorting and paging shared by every list endpoint.</summary>
public static class QueryableExtensions
{
    /// <summary>
    /// Orders by a property named at run time. The dashboard sends the DTO's
    /// camelCase property name (<c>totalSpent</c>), so these are applied to the
    /// projected query and matched case-insensitively. An unknown name is
    /// ignored rather than rejected, leaving the caller's default order intact.
    /// </summary>
    public static IQueryable<T> OrderByProperty<T>(this IQueryable<T> source, string? propertyName, bool descending)
    {
        if (string.IsNullOrWhiteSpace(propertyName)) return source;

        var property = typeof(T).GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        // Only sort by scalars — ordering by Lines would be meaningless in SQL.
        if (property is null || !IsSortable(property.PropertyType)) return source;

        var parameter = Expression.Parameter(typeof(T), "x");
        var selector = Expression.Lambda(Expression.Property(parameter, property), parameter);

        var call = Expression.Call(
            typeof(Queryable),
            descending ? nameof(Queryable.OrderByDescending) : nameof(Queryable.OrderBy),
            [typeof(T), property.PropertyType],
            source.Expression,
            Expression.Quote(selector));

        return source.Provider.CreateQuery<T>(call);
    }

    private static bool IsSortable(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)
            || t == typeof(DateTime) || t == typeof(DateOnly) || t == typeof(DateTimeOffset);
    }

    /// <summary>
    /// Runs <paramref name="source"/> as one page plus the total row count, both
    /// evaluated in SQL.
    /// </summary>
    /// <remarks>
    /// <c>Count</c> is always the full total, ignoring skip/take, so the client
    /// can size its pager from a single response. A null <paramref name="take"/>
    /// returns every matching row.
    /// </remarks>
    public static async Task<PagedList<T>> ToPagedListAsync<T>(
        this IQueryable<T> source, int skip, int? take, CancellationToken ct = default)
    {
        var count = await source.CountAsync(ct);

        if (skip > 0) source = source.Skip(skip);
        if (take is { } size) source = source.Take(size);

        return new PagedList<T>(await source.ToListAsync(ct), count);
    }

    /// <summary>Applies a <see cref="TableQuery"/>'s paging to a projected query.</summary>
    public static Task<PagedList<T>> ToPagedListAsync<T>(
        this IQueryable<T> source, TableQuery query, CancellationToken ct = default) =>
        source.ToPagedListAsync(query.EffectiveSkip, query.EffectiveTake, ct);
}
