using FluentMigrator;

namespace Aptabase.Data.Migrations;

[Migration(0025)]
public class UnhandledSessions : Migration
{
    public override void Up() => Execute.Sql("""
        ALTER TABLE monitoring_sessions DROP CONSTRAINT monitoring_sessions_status_check;
        ALTER TABLE monitoring_sessions ALTER COLUMN status TYPE varchar(9);
        ALTER TABLE monitoring_sessions ADD CONSTRAINT monitoring_sessions_status_check
            CHECK(status IN ('ok','exited','crashed','abnormal','unhandled'));
        """);

    public override void Down() => Execute.Sql("""
        DO $$ BEGIN
            IF EXISTS(SELECT 1 FROM monitoring_sessions WHERE status='unhandled') THEN
                RAISE EXCEPTION 'Cannot remove unhandled session support while such rows remain';
            END IF;
        END $$;
        ALTER TABLE monitoring_sessions DROP CONSTRAINT monitoring_sessions_status_check;
        ALTER TABLE monitoring_sessions ALTER COLUMN status TYPE varchar(8);
        ALTER TABLE monitoring_sessions ADD CONSTRAINT monitoring_sessions_status_check
            CHECK(status IN ('ok','exited','crashed','abnormal'));
        """);
}
