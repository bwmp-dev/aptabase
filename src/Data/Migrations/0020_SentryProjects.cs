using FluentMigrator;

namespace Aptabase.Data.Migrations;

[Migration(0020)]
public class SentryProjects : Migration
{
    public override void Up() => Execute.Sql("""
        CREATE TABLE sentry_projects (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            app_id varchar(22) NOT NULL REFERENCES apps(id) ON DELETE CASCADE,
            is_debug boolean NOT NULL,
            UNIQUE (app_id, is_debug)
        );
        """);
    public override void Down() => Delete.Table("sentry_projects");
}
