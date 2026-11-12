using FluentMigrator;

namespace Aptabase.Data.Migrations;

[Migration(0018)]
public class ErrorContext : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE error_receipts
                ADD COLUMN issue_id varchar(64),
                ADD COLUMN timestamp timestamptz,
                ADD COLUMN error_message text NOT NULL DEFAULT '',
                ADD COLUMN stack_trace text NOT NULL DEFAULT '',
                ADD COLUMN release text NOT NULL DEFAULT '',
                ADD COLUMN environment text NOT NULL DEFAULT '',
                ADD COLUMN platform text NOT NULL DEFAULT '',
                ADD COLUMN os_name text NOT NULL DEFAULT '',
                ADD COLUMN os_version text NOT NULL DEFAULT '',
                ADD COLUMN sdk_version text NOT NULL DEFAULT '',
                ADD COLUMN severity text NOT NULL DEFAULT '',
                ADD COLUMN kind text NOT NULL DEFAULT '';
            CREATE INDEX idx_error_receipts_issue ON error_receipts (app_id, is_debug, issue_id, timestamp DESC, event_id);
            """);
    }

    public override void Down()
    {
        Delete.Index("idx_error_receipts_issue").OnTable("error_receipts");
        foreach (var column in new[] { "issue_id", "timestamp", "error_message", "stack_trace", "release", "environment", "platform", "os_name", "os_version", "sdk_version", "severity", "kind" })
            Delete.Column(column).FromTable("error_receipts");
    }
}
