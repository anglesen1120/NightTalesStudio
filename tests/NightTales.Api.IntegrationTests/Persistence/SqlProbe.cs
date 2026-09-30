using System.Globalization;
using NightTales.TestKit.Database;
using Npgsql;
using Shouldly;

namespace NightTales.Api.IntegrationTests.Persistence;

/// <summary>
/// Raw-SQL probes for the schema tests: the constraint assertions have to bypass EF (the point is that
/// PostgreSQL itself rejects the row), so they run against the class database of
/// <see cref="TestDatabase"/> through Npgsql directly.
/// </summary>
internal static class SqlProbe
{
    /// <summary>Runs <paramref name="sql"/> expecting a PostgreSQL error and returns its SQLSTATE.</summary>
    public static string SqlState(TestDatabase database, string sql)
    {
        var exception = Should.Throw<PostgresException>(() => Execute(database, sql));

        return exception.SqlState;
    }

    /// <summary>Runs a <c>count(*)</c>-shaped statement and returns it as a <see cref="long"/>.</summary>
    public static long Count(TestDatabase database, string sql) =>
        Convert.ToInt64(Scalar(database, sql), CultureInfo.InvariantCulture);

    /// <summary>Runs a statement returning one text value and returns it (null becomes empty).</summary>
    public static string ScalarText(TestDatabase database, string sql) =>
        Convert.ToString(Scalar(database, sql), CultureInfo.InvariantCulture) ?? string.Empty;

    public static void Execute(TestDatabase database, string sql)
    {
        using var connection = Open(database);
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    public static List<string> QueryStrings(TestDatabase database, string sql)
    {
        using var connection = Open(database);
        using var command = new NpgsqlCommand(sql, connection);
        using var reader = command.ExecuteReader();

        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        }

        return values;
    }

    private static object? Scalar(TestDatabase database, string sql)
    {
        using var connection = Open(database);
        using var command = new NpgsqlCommand(sql, connection);

        var value = command.ExecuteScalar();

        return value is DBNull ? null : value;
    }

    private static NpgsqlConnection Open(TestDatabase database)
    {
        var connection = new NpgsqlConnection(database.ConnectionString);
        connection.Open();

        return connection;
    }
}
