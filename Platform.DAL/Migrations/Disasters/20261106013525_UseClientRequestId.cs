using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Platform.DAL.Contexts;

namespace Platform.DAL.Migrations.Disasters;

[DbContext(typeof(DisasterDbContext))]
[Migration("20261106013525_UseClientRequestId")]
public sealed class UseClientRequestId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn("OperationId", "Desastres", "ClientRequestId");
        migrationBuilder.DropIndex("IX_Desastres_OperationId", "Desastres");
        migrationBuilder.CreateIndex("IX_Desastres_ClientRequestId", "Desastres", "ClientRequestId", unique: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn("ClientRequestId", "Desastres", "OperationId");
        migrationBuilder.DropIndex("IX_Desastres_ClientRequestId", "Desastres");
        migrationBuilder.CreateIndex("IX_Desastres_OperationId", "Desastres", "OperationId", unique: true);
    }
}
