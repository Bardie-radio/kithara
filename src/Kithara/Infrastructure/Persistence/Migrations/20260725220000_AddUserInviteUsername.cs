using Kithara.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kithara.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(KitharaDbContext))]
[Migration("20260725220000_AddUserInviteUsername")]
public partial class AddUserInviteUsername : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Username",
            table: "users",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "InvitePasswordHash",
            table: "users",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "MustCompleteBinding",
            table: "users",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "InviteRolesJson",
            table: "users",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_users_Username",
            table: "users",
            column: "Username",
            unique: true,
            filter: "\"Username\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_user_auth_bindings_ProviderSlug_ExternalSubject",
            table: "user_auth_bindings",
            columns: new[] { "ProviderSlug", "ExternalSubject" },
            unique: true,
            filter: "\"ExternalSubject\" IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_user_auth_bindings_ProviderSlug_ExternalSubject",
            table: "user_auth_bindings");

        migrationBuilder.DropIndex(
            name: "IX_users_Username",
            table: "users");

        migrationBuilder.DropColumn(
            name: "InviteRolesJson",
            table: "users");

        migrationBuilder.DropColumn(
            name: "MustCompleteBinding",
            table: "users");

        migrationBuilder.DropColumn(
            name: "InvitePasswordHash",
            table: "users");

        migrationBuilder.DropColumn(
            name: "Username",
            table: "users");
    }
}
