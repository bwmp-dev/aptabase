using FluentMigrator;

namespace Aptabase.Data.Migrations;

[Migration(0015)]
public class ErrorIssues : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE error_issues (
                app_id varchar(22) NOT NULL REFERENCES apps(id) ON DELETE CASCADE,
                is_debug boolean NOT NULL,
                id varchar(64) NOT NULL,
                error_type text NOT NULL,
                error_message text NOT NULL,
                stack_trace text NOT NULL,
                status varchar(10) NOT NULL DEFAULT 'unresolved' CHECK (status IN ('unresolved', 'resolved')),
                occurrence_count bigint NOT NULL DEFAULT 1 CHECK (occurrence_count > 0),
                first_seen timestamptz NOT NULL,
                last_seen timestamptz NOT NULL,
                resolved_at timestamptz NULL,
                PRIMARY KEY (app_id, is_debug, id)
            );
            CREATE INDEX idx_error_issues_list ON error_issues (app_id, is_debug, last_seen DESC, id);
            """);
    }

    public override void Down() => Delete.Table("error_issues");
}
