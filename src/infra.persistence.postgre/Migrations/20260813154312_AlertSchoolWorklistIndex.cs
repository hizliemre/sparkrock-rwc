using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace infra.persistence.postgre.Migrations
{
    /// <inheritdoc />
    public partial class AlertSchoolWorklistIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_student_alerts_school_id",
                table: "student_alerts");

            migrationBuilder.CreateIndex(
                name: "ix_student_alerts_school_id_school_year_start",
                table: "student_alerts",
                columns: new[] { "school_id", "school_year_start" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_student_alerts_school_id_school_year_start",
                table: "student_alerts");

            migrationBuilder.CreateIndex(
                name: "ix_student_alerts_school_id",
                table: "student_alerts",
                column: "school_id");
        }
    }
}
