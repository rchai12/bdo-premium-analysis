using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BdoMarketTracker.Migrations
{
    /// <inheritdoc />
    public partial class AddPredictionFeedbackLoop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "correction_factors",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    item_id = table.Column<int>(type: "integer", nullable: false),
                    window = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    factor = table.Column<double>(type: "double precision", nullable: false),
                    sample_count = table.Column<int>(type: "integer", nullable: false),
                    last_updated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_correction_factors", x => x.id);
                    table.ForeignKey(
                        name: "FK_correction_factors_tracked_items_item_id",
                        column: x => x.item_id,
                        principalTable: "tracked_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "velocity_predictions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    item_id = table.Column<int>(type: "integer", nullable: false),
                    window = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    predicted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    predicted_sales_per_hour = table.Column<double>(type: "double precision", nullable: false),
                    predicted_preorders = table.Column<long>(type: "bigint", nullable: false),
                    evaluation_due_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    actual_sales_per_hour = table.Column<double>(type: "double precision", nullable: true),
                    actual_preorders = table.Column<long>(type: "bigint", nullable: true),
                    accuracy_ratio = table.Column<double>(type: "double precision", nullable: true),
                    evaluated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_velocity_predictions", x => x.id);
                    table.ForeignKey(
                        name: "FK_velocity_predictions_tracked_items_item_id",
                        column: x => x.item_id,
                        principalTable: "tracked_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_correction_factors_item_id_window",
                table: "correction_factors",
                columns: new[] { "item_id", "window" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_velocity_predictions_evaluation_due_at",
                table: "velocity_predictions",
                column: "evaluation_due_at",
                filter: "evaluated_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_velocity_predictions_item_id_window_predicted_at",
                table: "velocity_predictions",
                columns: new[] { "item_id", "window", "predicted_at" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "correction_factors");

            migrationBuilder.DropTable(
                name: "velocity_predictions");
        }
    }
}
