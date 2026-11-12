using FluentMigrator;

namespace Aptabase.Data.Migrations;

[Migration(0019)]
public class AddErrorOutbox : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE error_outbox (
                app_id varchar(22) NOT NULL REFERENCES apps(id) ON DELETE CASCADE,
                is_debug boolean NOT NULL,
                event_id uuid NOT NULL,
                payload jsonb NOT NULL,
                created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (app_id, is_debug, event_id)
            );
            CREATE INDEX idx_error_outbox_created ON error_outbox (created_at);
            """);
    }

    public override void Down() => Delete.Table("error_outbox");
}
