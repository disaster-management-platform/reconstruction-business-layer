using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.DAL.Migrations.Reconstruction
{
    /// <inheritdoc />
    public partial class ReconstructionIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReconstructionProposals_DisasterId_Zone_AssetType_Damage",
                table: "ReconstructionProposals");

            migrationBuilder.AddColumn<Guid>(
                name: "RequestId",
                table: "ReconstructionReports",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AfterJson",
                table: "ReconstructionAudit",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BeforeJson",
                table: "ReconstructionAudit",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DataJson",
                table: "ReconstructionAudit",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.CreateIndex(
                name: "IX_ReconstructionReports_RequestId",
                table: "ReconstructionReports",
                column: "RequestId",
                unique: true,
                filter: "\"RequestId\" IS NOT NULL");

            migrationBuilder.Sql("""
                UPDATE ReconstructionProposals SET Status=2, Version=Version+1
                WHERE Id IN (
                    SELECT Id FROM (
                        SELECT Id, ROW_NUMBER() OVER (PARTITION BY Zone, AssetType ORDER BY CreatedAt, Id) AS ordinal
                        FROM ReconstructionProposals WHERE Status=0 AND IsSelectedForReconstruction=0
                    ) WHERE ordinal > 1
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ReconstructionProposals_DisasterId_Zone_AssetType",
                table: "ReconstructionProposals",
                columns: new[] { "DisasterId", "Zone", "AssetType" },
                unique: true,
                filter: "\"Status\" = 0 AND \"IsSelectedForReconstruction\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReconstructionReports_RequestId",
                table: "ReconstructionReports");

            migrationBuilder.DropIndex(
                name: "IX_ReconstructionProposals_DisasterId_Zone_AssetType",
                table: "ReconstructionProposals");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "ReconstructionReports");

            migrationBuilder.DropColumn(
                name: "AfterJson",
                table: "ReconstructionAudit");

            migrationBuilder.DropColumn(
                name: "BeforeJson",
                table: "ReconstructionAudit");

            migrationBuilder.DropColumn(
                name: "DataJson",
                table: "ReconstructionAudit");

            migrationBuilder.CreateIndex(
                name: "IX_ReconstructionProposals_DisasterId_Zone_AssetType_Damage",
                table: "ReconstructionProposals",
                columns: new[] { "DisasterId", "Zone", "AssetType", "Damage" },
                unique: true,
                filter: "\"Status\" = 0 AND \"IsSelectedForReconstruction\" = 0");
        }
    }
}
