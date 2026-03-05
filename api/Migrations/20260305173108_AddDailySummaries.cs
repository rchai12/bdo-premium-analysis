using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BdoMarketTracker.Migrations
{
    /// <inheritdoc />
    public partial class AddDailySummaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "daily_summaries",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    item_id = table.Column<int>(type: "integer", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    sales_count = table.Column<long>(type: "bigint", nullable: false),
                    avg_base_price = table.Column<long>(type: "bigint", nullable: false),
                    avg_preorders = table.Column<long>(type: "bigint", nullable: false),
                    snapshot_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_summaries", x => x.id);
                    table.ForeignKey(
                        name: "FK_daily_summaries_tracked_items_item_id",
                        column: x => x.item_id,
                        principalTable: "tracked_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_daily_summaries_item_id_date",
                table: "daily_summaries",
                columns: new[] { "item_id", "date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "daily_summaries");
        }
    }
}
