using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.DAL.Migrations.Reconstruction
{
    /// <inheritdoc />
    public partial class IdentifyDonationAllocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FundingSourceId",
                table: "ReconstructionProjects",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "donations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FundingSourceId",
                table: "ReconstructionProjects");
        }
    }
}
