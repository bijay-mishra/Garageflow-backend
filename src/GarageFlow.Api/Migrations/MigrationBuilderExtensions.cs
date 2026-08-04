using Microsoft.EntityFrameworkCore.Migrations;

namespace GarageFlow.Api.Migrations;

internal static class MigrationBuilderExtensions
{
    /// <summary>
    /// Runs raw SQL that reads a column added earlier in the same migration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Plain <see cref="MigrationBuilder.Sql(string, bool)"/> is correct under
    /// <c>dotnet ef database update</c>, which sends each command to the server
    /// on its own. It is <b>not</b> correct in a generated SQL script: there
    /// every statement of a migration lands in one batch, and SQL Server
    /// compiles a whole batch before executing any of it. A statement naming a
    /// column that the <c>ALTER TABLE</c> three lines above has not created yet
    /// fails to compile — "Invalid column name" — and the script dies on the
    /// first fresh database it is ever run against.
    /// </para>
    /// <para>
    /// Deferred name resolution does not save this. It covers tables that do
    /// not exist yet, not columns missing from a table that does.
    /// </para>
    /// <para>
    /// Wrapping the statement in <c>EXEC</c> makes it a string until the moment
    /// it runs, so it is compiled after the schema change ahead of it has taken
    /// effect. Behaviour under <c>database update</c> is unchanged; this only
    /// fixes the scripted path — which is the path a deployment uses.
    /// </para>
    /// </remarks>
    public static void DeferredSql(this MigrationBuilder migrationBuilder, string sql) =>
        // Doubled, because the SQL is becoming a single-quoted string literal
        // and every quote inside it — 'Bike', 'DEMO' — would otherwise end it.
        migrationBuilder.Sql($"EXEC(N'{sql.Replace("'", "''")}')");
}
