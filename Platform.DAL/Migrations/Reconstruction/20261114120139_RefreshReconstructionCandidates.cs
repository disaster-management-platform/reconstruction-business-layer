using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.DAL.Migrations.Reconstruction
{
    /// <inheritdoc />
    public partial class RefreshReconstructionCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReconstructionProposals_DisasterId_Zone_AssetType_Damage",
                table: "ReconstructionProposals");

            migrationBuilder.CreateIndex(
                name: "IX_ReconstructionProposals_DisasterId_Zone_AssetType_Damage",
                table: "ReconstructionProposals",
                columns: new[] { "DisasterId", "Zone", "AssetType", "Damage" },
                unique: true,
                filter: "\"Status\" = 0 AND \"IsSelectedForReconstruction\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReconstructionProposals_DisasterId_Zone_AssetType_Damage",
                table: "ReconstructionProposals");

            migrationBuilder.CreateIndex(
                name: "IX_ReconstructionProposals_DisasterId_Zone_AssetType_Damage",
                table: "ReconstructionProposals",
                columns: new[] { "DisasterId", "Zone", "AssetType", "Damage" },
                unique: true);
        }
    }
}
