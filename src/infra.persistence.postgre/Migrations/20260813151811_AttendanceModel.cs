using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace infra.persistence.postgre.Migrations
{
    /// <inheritdoc />
    public partial class AttendanceModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "attendance_submission_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    school_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attend_date = table.Column<DateOnly>(type: "date", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    record_count = table.Column<int>(type: "integer", nullable: false),
                    submitted_by = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attendance_submission_logs", x => x.id);
                    table.CheckConstraint("ck_submission_logs_record_count_not_negative", "record_count >= 0");
                    table.ForeignKey(
                        name: "fk_submission_logs_schools_school_id",
                        column: x => x.school_id,
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "legacy_import_anomalies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    legacy_id = table.Column<int>(type: "integer", nullable: true),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    anomaly_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_legacy_import_anomalies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "student_alerts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    school_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    school_year_start = table.Column<int>(type: "integer", nullable: false),
                    absence_count = table.Column<int>(type: "integer", nullable: false),
                    threshold_at_raise = table.Column<int>(type: "integer", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolution_source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    resolution_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_student_alerts", x => x.id);
                    table.CheckConstraint("ck_student_alerts_absence_count_not_negative", "absence_count >= 0");
                    table.CheckConstraint("ck_student_alerts_resolution_consistent", "(resolved_at IS NULL AND resolution_source IS NULL) OR (resolved_at IS NOT NULL AND resolution_source IS NOT NULL)");
                    table.CheckConstraint("ck_student_alerts_school_year_start", "school_year_start BETWEEN 1900 AND 2100");
                    table.ForeignKey(
                        name: "fk_student_alerts_schools_school_id",
                        column: x => x.school_id,
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_student_alerts_students_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "student_attendance_summaries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    school_id = table.Column<Guid>(type: "uuid", nullable: false),
                    school_year_start = table.Column<int>(type: "integer", nullable: false),
                    total_absences = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_student_attendance_summaries", x => x.id);
                    table.CheckConstraint("ck_student_attendance_summaries_school_year_start", "school_year_start BETWEEN 1900 AND 2100");
                    table.CheckConstraint("ck_student_attendance_summaries_total_absences_not_negative", "total_absences >= 0");
                    table.ForeignKey(
                        name: "fk_summaries_schools_school_id",
                        column: x => x.school_id,
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_summaries_students_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "student_attendances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    school_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attend_date = table.Column<DateOnly>(type: "date", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attendance_code_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attend_code = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    attend_code_description = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_absent = table.Column<bool>(type: "boolean", nullable: false),
                    is_excused = table.Column<bool>(type: "boolean", nullable: false),
                    minutes_late = table.Column<int>(type: "integer", nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    legacy_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    modified_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_student_attendances", x => x.id);
                    table.CheckConstraint("ck_student_attendances_minutes_late_not_negative", "minutes_late IS NULL OR minutes_late >= 0");
                    table.ForeignKey(
                        name: "fk_student_attendances_attendance_codes_attendance_code_id",
                        column: x => x.attendance_code_id,
                        principalTable: "attendance_codes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_student_attendances_school_terms_term_id",
                        column: x => x.term_id,
                        principalTable: "school_terms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_student_attendances_schools_school_id",
                        column: x => x.school_id,
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_student_attendances_students_student_id",
                        column: x => x.student_id,
                        principalTable: "students",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_student_attendances_submission_logs_submission_id",
                        column: x => x.submission_id,
                        principalTable: "attendance_submission_logs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_submission_logs_school_id_idempotency_key",
                table: "attendance_submission_logs",
                columns: new[] { "school_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_submission_logs_school_id_submitted_at_id",
                table: "attendance_submission_logs",
                columns: new[] { "school_id", "submitted_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_import_anomalies_batch_id_anomaly_code",
                table: "legacy_import_anomalies",
                columns: new[] { "batch_id", "anomaly_code" });

            migrationBuilder.CreateIndex(
                name: "ix_student_alerts_open_episode",
                table: "student_alerts",
                columns: new[] { "student_id", "alert_type", "school_year_start", "school_id" },
                unique: true,
                filter: "resolved_at IS NULL AND is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_student_alerts_school_id",
                table: "student_alerts",
                column: "school_id");

            migrationBuilder.CreateIndex(
                name: "ix_student_alerts_student_id_school_year_start",
                table: "student_alerts",
                columns: new[] { "student_id", "school_year_start" });

            migrationBuilder.CreateIndex(
                name: "ix_student_attendance_summaries_school_id",
                table: "student_attendance_summaries",
                column: "school_id");

            migrationBuilder.CreateIndex(
                name: "ix_summaries_student_id_school_year_start",
                table: "student_attendance_summaries",
                columns: new[] { "student_id", "school_year_start" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_student_attendances_attendance_code_id",
                table: "student_attendances",
                column: "attendance_code_id");

            migrationBuilder.CreateIndex(
                name: "ix_student_attendances_legacy_id",
                table: "student_attendances",
                column: "legacy_id",
                unique: true,
                filter: "legacy_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_student_attendances_school_id_attend_date",
                table: "student_attendances",
                columns: new[] { "school_id", "attend_date" });

            migrationBuilder.CreateIndex(
                name: "ix_student_attendances_student_id",
                table: "student_attendances",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "ix_student_attendances_student_id_attend_date",
                table: "student_attendances",
                columns: new[] { "student_id", "attend_date" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_student_attendances_submission_id",
                table: "student_attendances",
                column: "submission_id");

            migrationBuilder.CreateIndex(
                name: "ix_student_attendances_term_id",
                table: "student_attendances",
                column: "term_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "legacy_import_anomalies");

            migrationBuilder.DropTable(
                name: "student_alerts");

            migrationBuilder.DropTable(
                name: "student_attendance_summaries");

            migrationBuilder.DropTable(
                name: "student_attendances");

            migrationBuilder.DropTable(
                name: "attendance_submission_logs");
        }
    }
}
