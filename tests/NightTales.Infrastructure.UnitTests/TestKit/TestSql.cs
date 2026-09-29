using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace NightTales.Infrastructure.UnitTests.TestKit;

/// <summary>Raw SQL helpers for the schema and constraint assertions.</summary>
internal static class TestSql
{
    public static List<string> QueryStrings(DbContext context, string sql)
    {
        ArgumentNullException.ThrowIfNull(context);

        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    public static void Execute(DbContext context, string sql)
    {
        ArgumentNullException.ThrowIfNull(context);

        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Runs <paramref name="sql"/> expecting a PostgreSQL error and returns its SQLSTATE.</summary>
    public static string SqlStateOf(DbContext context, string sql)
    {
        var exception = Shouldly.Should.Throw<Exception>(() => Execute(context, sql));

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException { SqlState: { Length: > 0 } sqlState })
            {
                return sqlState;
            }
        }

        throw new InvalidOperationException($"The statement did not fail with a PostgreSQL error: {exception.Message}", exception);
    }
}
