using System.Data;
using System.Runtime.CompilerServices;
using Dapper;

namespace OmiPlatform.Storage;

/// <summary>
/// Teaches Dapper about <see cref="DateOnly"/>.
/// </summary>
/// <remarks>
/// Npgsql maps <c>date</c> to <see cref="DateOnly"/> happily; Dapper does not know the type at all
/// and throws <c>"cannot be used as a parameter value"</c> on the way in, then refuses to match a
/// record constructor on the way out. Registering a handler fixes both directions.
/// <para>
/// A module initializer rather than a DI call: every repository in this assembly depends on it,
/// and a registration that has to be remembered is one that eventually is not.
/// </para>
/// </remarks>
internal static class DapperConfiguration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Configure() => SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

    private sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value;
        }

        public override DateOnly Parse(object value) => value switch
        {
            DateOnly day => day,
            DateTime timestamp => DateOnly.FromDateTime(timestamp),
            string text => DateOnly.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new InvalidCastException($"Cannot convert {value.GetType()} to DateOnly."),
        };
    }
}
