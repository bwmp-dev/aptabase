using FluentMigrator;

namespace Aptabase.Data.Migrations;

[Migration(0017)]
public class AppTransactions : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE app_transactions (
                app_id varchar(22) NOT NULL REFERENCES apps(id) ON DELETE CASCADE,
                is_debug boolean NOT NULL,
                event_id uuid NOT NULL,
                timestamp timestamptz NOT NULL,
                received_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
                name text NOT NULL,
                operation text NOT NULL,
                duration_ms double precision NOT NULL CHECK (duration_ms >= 0 AND duration_ms <= 86400000),
                status varchar(9) NOT NULL CHECK (status IN ('ok', 'error', 'cancelled')),
                release text NOT NULL,
                environment text NOT NULL,
                platform text NOT NULL,
                spans jsonb NOT NULL,
                PRIMARY KEY (app_id, is_debug, event_id)
            );
            CREATE INDEX idx_app_transactions_time ON app_transactions (app_id, is_debug, timestamp DESC, event_id);
            CREATE INDEX idx_app_transactions_retention ON app_transactions (received_at);
            """);
    }

    public override void Down() => Delete.Table("app_transactions");
}
