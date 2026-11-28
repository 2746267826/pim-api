using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pim.Infrastructure.Data.Migrations
{
    /// <summary>
    /// J09 follow-up: normalize usage-limit rows created before the Token-only contract.
    ///
    /// <para>
    /// SDK calls do not produce a trusted EstimatedCost/Currency, so cost columns are diagnostic
    /// only and must not drive enforcement. An enabled rule without a Token limit cannot be
    /// evaluated; leaving it enabled would either allow silently or block every call with an
    /// unknown usage marker. Clear the unsupported cost fields, then disable only those invalid
    /// enabled rules. Rules with a Token limit stay enabled and keep their Token value.
    /// </para>
    /// </summary>
    public partial class NormalizeLegacyAiUsageCostRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE ai_usage_limit_rules
                SET cost_limit = NULL,
                    currency = NULL,
                    revision = gen_random_uuid(),
                    updated_at = now()
                WHERE cost_limit IS NOT NULL OR currency IS NOT NULL;

                UPDATE ai_usage_limit_rules
                SET enabled = FALSE,
                    revision = gen_random_uuid(),
                    updated_at = now()
                WHERE enabled AND token_limit IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Historical cost values and enabled flags cannot be reconstructed. The runtime
            // fail-closed check remains authoritative for any invalid legacy row.
        }
    }
}
