using FluentMigrator;

namespace Aptabase.Data.Migrations;

[Migration(0024)]
public class SentrySessionHealth : Migration
{
    public override void Up() => Execute.Sql("""
        ALTER TABLE apps ADD COLUMN monitoring_session_salt bytea
            CHECK (monitoring_session_salt IS NULL OR octet_length(monitoring_session_salt)=32);
        CREATE TABLE monitoring_sessions (
            app_id varchar(22) NOT NULL REFERENCES apps(id) ON DELETE CASCADE,
            is_debug boolean NOT NULL, session_key char(64) NOT NULL,
            started_at timestamptz NOT NULL, updated_at timestamptz NOT NULL,
            sequence bigint NOT NULL CHECK(sequence>=0), saw_init boolean NOT NULL,
            status varchar(8) NOT NULL CHECK(status IN ('ok','exited','crashed','abnormal')),
            errors bigint NOT NULL CHECK(errors>=0), duration_seconds double precision NOT NULL CHECK(duration_seconds>=0),
            release text NOT NULL, environment text NOT NULL, received_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY(app_id,is_debug,session_key));
        CREATE INDEX idx_monitoring_sessions_time ON monitoring_sessions(app_id,is_debug,started_at);
        CREATE INDEX idx_monitoring_sessions_retention ON monitoring_sessions(received_at);
        CREATE TABLE monitoring_client_reports (
            app_id varchar(22) NOT NULL REFERENCES apps(id) ON DELETE CASCADE,
            is_debug boolean NOT NULL, report_key char(64) NOT NULL,
            reported_at timestamptz NOT NULL, received_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
            release text NOT NULL, environment text NOT NULL, counters jsonb NOT NULL,
            PRIMARY KEY(app_id,is_debug,report_key));
        CREATE INDEX idx_monitoring_reports_time ON monitoring_client_reports(app_id,is_debug,reported_at);
        CREATE INDEX idx_monitoring_reports_retention ON monitoring_client_reports(received_at);
        """);

    public override void Down() => Execute.Sql("DROP TABLE monitoring_client_reports; DROP TABLE monitoring_sessions; ALTER TABLE apps DROP COLUMN monitoring_session_salt;");
}
