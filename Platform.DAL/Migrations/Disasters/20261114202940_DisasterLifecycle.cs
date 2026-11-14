using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.DAL.Migrations.Disasters
{
    /// <inheritdoc />
    public partial class DisasterLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Phase",
                table: "Desastres",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "Radio",
                table: "Desastres",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "Desastres",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "DisasterPhaseAudit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisasterId = table.Column<int>(type: "INTEGER", nullable: false),
                    Before = table.Column<int>(type: "INTEGER", nullable: false),
                    After = table.Column<int>(type: "INTEGER", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DisasterPhaseAudit", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DisasterPhaseAudit_DisasterId_Version",
                table: "DisasterPhaseAudit",
                columns: new[] { "DisasterId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DisasterPhaseAudit");

            migrationBuilder.DropColumn(
                name: "Phase",
                table: "Desastres");

            migrationBuilder.DropColumn(
                name: "Radio",
                table: "Desastres");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Desastres");
        }
    }
}
