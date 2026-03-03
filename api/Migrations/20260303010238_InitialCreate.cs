using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BdoMarketTracker.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tracked_items",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    grade = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracked_items", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "trade_snapshots",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    item_id = table.Column<int>(type: "integer", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    total_trades = table.Column<long>(type: "bigint", nullable: false),
                    current_stock = table.Column<long>(type: "bigint", nullable: false),
                    base_price = table.Column<long>(type: "bigint", nullable: false),
                    last_sold_price = table.Column<long>(type: "bigint", nullable: false),
                    total_preorders = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trade_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "FK_trade_snapshots_tracked_items_item_id",
                        column: x => x.item_id,
                        principalTable: "tracked_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_trade_snapshots_item_id_recorded_at",
                table: "trade_snapshots",
                columns: new[] { "item_id", "recorded_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trade_snapshots");

            migrationBuilder.DropTable(
                name: "tracked_items");
        }
    }
}
