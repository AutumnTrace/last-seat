using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LastSeat.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "allocation_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    performance_id = table.Column<int>(type: "integer", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    seat_ids = table.Column<int[]>(type: "integer[]", nullable: false),
                    lock_wait_ms = table.Column<double>(type: "double precision", nullable: true),
                    detail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_allocation_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "demo_clock",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    offset_ms = table.Column<long>(type: "bigint", nullable: false),
                    frozen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cleanup_paused = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_demo_clock", x => x.id);
                    table.CheckConstraint("ck_demo_clock_single_row", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "performances",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    venue = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    starts_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    hold_seconds = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_performances", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bookings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    performance_id = table.Column<int>(type: "integer", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    hold_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reference = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bookings", x => x.id);
                    table.ForeignKey(
                        name: "fk_bookings_performances_performance_id",
                        column: x => x.performance_id,
                        principalTable: "performances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "holds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    performance_id = table.Column<int>(type: "integer", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_holds", x => x.id);
                    table.CheckConstraint("ck_holds_status", "status in ('active', 'confirmed', 'released', 'replaced')");
                    table.ForeignKey(
                        name: "fk_holds_performances_performance_id",
                        column: x => x.performance_id,
                        principalTable: "performances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_holds_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "request_keys",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    operation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: false),
                    response_json = table.Column<string>(type: "json", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_request_keys", x => new { x.session_id, x.key });
                    table.ForeignKey(
                        name: "fk_request_keys_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "seats",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    performance_id = table.Column<int>(type: "integer", nullable: false),
                    section = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    row_label = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    block = table.Column<int>(type: "integer", nullable: false),
                    x = table.Column<float>(type: "real", nullable: false),
                    y = table.Column<float>(type: "real", nullable: false),
                    hold_id = table.Column<Guid>(type: "uuid", nullable: true),
                    hold_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    booking_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seats", x => x.id);
                    table.CheckConstraint("ck_seats_booked_or_held", "booking_id is null or hold_id is null");
                    table.CheckConstraint("ck_seats_hold_columns", "(hold_id is null) = (hold_expires_at is null)");
                    table.ForeignKey(
                        name: "fk_seats_bookings_booking_id",
                        column: x => x.booking_id,
                        principalTable: "bookings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_seats_holds_hold_id",
                        column: x => x.hold_id,
                        principalTable: "holds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_seats_performances_performance_id",
                        column: x => x.performance_id,
                        principalTable: "performances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "hold_seats",
                columns: table => new
                {
                    hold_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seat_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hold_seats", x => new { x.hold_id, x.seat_id });
                    table.ForeignKey(
                        name: "fk_hold_seats_holds_hold_id",
                        column: x => x.hold_id,
                        principalTable: "holds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_hold_seats_seats_seat_id",
                        column: x => x.seat_id,
                        principalTable: "seats",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_allocation_events_performance_id_id",
                table: "allocation_events",
                columns: new[] { "performance_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_bookings_hold_id",
                table: "bookings",
                column: "hold_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bookings_performance_id",
                table: "bookings",
                column: "performance_id");

            migrationBuilder.CreateIndex(
                name: "ix_bookings_reference",
                table: "bookings",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_hold_seats_seat_id",
                table: "hold_seats",
                column: "seat_id");

            migrationBuilder.CreateIndex(
                name: "ix_holds_performance_id_session_id",
                table: "holds",
                columns: new[] { "performance_id", "session_id" },
                unique: true,
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "ix_holds_session_id",
                table: "holds",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_seats_booking_id",
                table: "seats",
                column: "booking_id");

            migrationBuilder.CreateIndex(
                name: "ix_seats_hold_id",
                table: "seats",
                column: "hold_id");

            migrationBuilder.CreateIndex(
                name: "ix_seats_performance_id_row_label_number",
                table: "seats",
                columns: new[] { "performance_id", "row_label", "number" },
                unique: true);

            // The one clock every allocation decision reads. clock_timestamp() rather than now():
            // now() is the transaction start time, which would ignore time spent waiting for a lock.
            // demo_clock can pin it (tests) or move it forward (the demo); by default it does neither.
            migrationBuilder.Sql("""
                create function lastseat_now() returns timestamptz
                language sql volatile
                as $$
                    select coalesce(
                        (select frozen_at from demo_clock where id = 1),
                        clock_timestamp() + coalesce((select offset_ms from demo_clock where id = 1), 0) * interval '1 millisecond')
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("drop function if exists lastseat_now();");
            migrationBuilder.DropTable(
                name: "allocation_events");

            migrationBuilder.DropTable(
                name: "demo_clock");

            migrationBuilder.DropTable(
                name: "hold_seats");

            migrationBuilder.DropTable(
                name: "request_keys");

            migrationBuilder.DropTable(
                name: "seats");

            migrationBuilder.DropTable(
                name: "bookings");

            migrationBuilder.DropTable(
                name: "holds");

            migrationBuilder.DropTable(
                name: "performances");

            migrationBuilder.DropTable(
                name: "sessions");
        }
    }
}
