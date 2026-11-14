using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Platform.DAL.Contexts;

namespace Platform.DAL.Migrations.Disasters;

[DbContext(typeof(DisasterDbContext))]
[Migration("20261106013504_AddOperationId")]
public sealed class AddOperationId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("OperationId", "Desastres", "TEXT", nullable: true);
        migrationBuilder.CreateIndex("IX_Desastres_OperationId", "Desastres", "OperationId", unique: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_Desastres_OperationId", "Desastres");
        migrationBuilder.DropColumn("OperationId", "Desastres");
    }
}
