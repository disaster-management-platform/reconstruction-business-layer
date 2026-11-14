using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.DAL.Migrations.Reconstruction
{
    /// <inheritdoc />
    public partial class ConsumerScopedInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_InboxMessages",
                table: "InboxMessages");

            migrationBuilder.AddColumn<string>(
                name: "ConsumerName",
                table: "InboxMessages",
                type: "TEXT",
                maxLength: 160,
                nullable: false,
                defaultValue: "reconstruction.timeline");

            migrationBuilder.AddColumn<string>(
                name: "OutcomeJson",
                table: "InboxMessages",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_InboxMessages",
                table: "InboxMessages",
                columns: new[] { "ConsumerName", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_InboxMessages",
                table: "InboxMessages");

            migrationBuilder.DropColumn(
                name: "ConsumerName",
                table: "InboxMessages");

            migrationBuilder.DropColumn(
                name: "OutcomeJson",
                table: "InboxMessages");

            migrationBuilder.AddPrimaryKey(
                name: "PK_InboxMessages",
                table: "InboxMessages",
                column: "Id");
        }
    }
}
