using FluentMigrator;

namespace Aptabase.Data.Migrations;

[Migration(0021)]
public class AddMonitoringControls : Migration
{
    public override void Up() => Execute.Sql("""
        ALTER TABLE apps ADD COLUMN capture_errors boolean NOT NULL DEFAULT true,
            ADD COLUMN capture_performance boolean NOT NULL DEFAULT true,
            ADD COLUMN forward_raw_errors boolean NOT NULL DEFAULT true,
            ADD COLUMN monitoring_retention_days integer NOT NULL DEFAULT 30 CHECK (monitoring_retention_days BETWEEN 1 AND 30),
            ADD COLUMN local_alerts_enabled boolean NOT NULL DEFAULT false,
            ADD COLUMN alert_cooldown_minutes integer NOT NULL DEFAULT 60 CHECK (alert_cooldown_minutes BETWEEN 1 AND 1440);
        ALTER TABLE error_issues ADD COLUMN last_reopened_event_id uuid;
        CREATE TABLE monitoring_alert_state (
            app_id varchar(22) NOT NULL REFERENCES apps(id) ON DELETE CASCADE,
            is_debug boolean NOT NULL, issue_id varchar(64) NOT NULL, last_notified_at timestamptz NOT NULL,
            PRIMARY KEY (app_id, is_debug, issue_id));
        CREATE TABLE monitoring_alerts (
            app_id varchar(22) NOT NULL REFERENCES apps(id) ON DELETE CASCADE,
            is_debug boolean NOT NULL, event_id uuid NOT NULL, issue_id varchar(64) NOT NULL,
            kind varchar(16) NOT NULL CHECK (kind IN ('new_issue', 'regression')),
            created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP, read_at timestamptz,
            PRIMARY KEY (app_id, is_debug, event_id));
        CREATE INDEX idx_monitoring_alerts_created ON monitoring_alerts (app_id, is_debug, created_at DESC);
        """);

    public override void Down() => Execute.Sql("""
        DROP TABLE monitoring_alerts;
        DROP TABLE monitoring_alert_state;
        ALTER TABLE error_issues DROP COLUMN last_reopened_event_id;
        ALTER TABLE apps DROP COLUMN capture_errors, DROP COLUMN capture_performance, DROP COLUMN forward_raw_errors,
            DROP COLUMN monitoring_retention_days, DROP COLUMN local_alerts_enabled, DROP COLUMN alert_cooldown_minutes;
        """);
}
