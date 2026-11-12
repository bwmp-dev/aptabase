using FluentMigrator;

namespace Aptabase.Data.Migrations;

[Migration(0016)]
public class ErrorReceipts : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE error_receipts (
                app_id varchar(22) NOT NULL REFERENCES apps(id) ON DELETE CASCADE,
                is_debug boolean NOT NULL,
                event_id uuid NOT NULL,
                received_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (app_id, is_debug, event_id)
            );
            CREATE INDEX idx_error_receipts_received_at ON error_receipts (received_at);
            """);
    }

    public override void Down() => Delete.Table("error_receipts");
}
