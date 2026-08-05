using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageFlow.Api.Migrations
{
    /// <summary>
    /// Puts the companies that already exist into the customer app's directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Listing began as opt-in, defaulting to off, so that a workshop which
    /// bought this to run its books was never advertised without agreeing to
    /// be. The intent was right and the result was not: nobody knew the switch
    /// existed, so the directory a customer opens contained one garage — the
    /// seeded demo — while every real company sat invisible.
    /// </para>
    /// <para>
    /// Listing is opt-out from here. This flips the companies already on file;
    /// new ones are created listed. The switch on the Workshop screen still
    /// works, so a garage that does not want to be found can still say so — it
    /// now has to say so, rather than having it assumed.
    /// </para>
    /// <para>
    /// A plain <c>Sql</c> rather than <c>DeferredSql</c>: <c>IsListed</c> was
    /// added by an earlier migration, so it exists in the database before this
    /// migration's batch is compiled. The deferred form is only needed when a
    /// migration reads a column it is itself adding.
    /// </para>
    /// </remarks>
    public partial class ListExistingGarages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The blank-coded row is not a company — see SuperAdminController's
            // Companies endpoint, which filters it out of the operator console
            // for the same reason. It must never reach the public directory.
            migrationBuilder.Sql(
                "UPDATE Workshops SET IsListed = 1 WHERE CompanyCode <> '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately not the inverse. Un-listing everything on a rollback
            // would also hide the garages that had opted in under the old
            // default, and this migration cannot tell those apart from the ones
            // it switched on itself.
        }
    }
}
