using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.DAL.Migrations.Reconstruction
{
    /// <inheritdoc />
    public partial class AddProposalVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "ReconstructionProposals",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "ReconstructionProposals");
        }
    }
}
