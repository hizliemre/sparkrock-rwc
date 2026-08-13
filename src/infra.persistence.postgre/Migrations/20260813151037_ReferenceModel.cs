using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace infra.persistence.postgre.Migrations
{
    /// <inheritdoc />
    public partial class ReferenceModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "attendance_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    value = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    description = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_absent = table.Column<bool>(type: "boolean", nullable: false),
                    is_excused = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    legacy_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attendance_codes", x => x.id);
                    table.CheckConstraint("ck_attendance_codes_value_upper", "value = upper(value)");
                });

            migrationBuilder.CreateTable(
                name: "schools",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    absence_alert_threshold = table.Column<int>(type: "integer", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    legacy_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schools", x => x.id);
                    table.CheckConstraint("ck_schools_absence_alert_threshold_positive", "absence_alert_threshold IS NULL OR absence_alert_threshold >= 1");
                });

            migrationBuilder.CreateTable(
                name: "school_terms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    school_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    legacy_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_school_terms", x => x.id);
                    table.CheckConstraint("ck_school_terms_end_date_not_before_start_date", "end_date >= start_date");
                    table.ForeignKey(
                        name: "fk_school_terms_schools_school_id",
                        column: x => x.school_id,
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "students",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    school_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    last_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    grade = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    legacy_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_students", x => x.id);
                    table.ForeignKey(
                        name: "fk_students_schools_school_id",
                        column: x => x.school_id,
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attendance_codes_legacy_id",
                table: "attendance_codes",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_attendance_codes_value",
                table: "attendance_codes",
                column: "value",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_school_terms_legacy_id",
                table: "school_terms",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_school_terms_school_id_start_date_end_date",
                table: "school_terms",
                columns: new[] { "school_id", "start_date", "end_date" });

            migrationBuilder.CreateIndex(
                name: "ix_schools_legacy_id",
                table: "schools",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_students_legacy_id",
                table: "students",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_students_school_id_is_active",
                table: "students",
                columns: new[] { "school_id", "is_active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attendance_codes");

            migrationBuilder.DropTable(
                name: "school_terms");

            migrationBuilder.DropTable(
                name: "students");

            migrationBuilder.DropTable(
                name: "schools");
        }
    }
}
