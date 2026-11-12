using FluentMigrator;
namespace Aptabase.Data.Migrations;

[Migration(0022)]
public class NativeSymbols : Migration
{
    public override void Up() => Execute.Sql("""
        CREATE TABLE native_symbols (
            id uuid PRIMARY KEY, app_id varchar(22) NOT NULL REFERENCES apps(id) ON DELETE CASCADE,
            is_debug boolean NOT NULL, content_hash char(64) NOT NULL, content bytea NOT NULL,
            size_bytes integer NOT NULL CHECK(size_bytes BETWEEN 64 AND 8388608),
            build_id varchar(64), status varchar(8) NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','ready','invalid')),
            symbol_index jsonb, created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
            expires_at timestamptz NOT NULL,
            UNIQUE(app_id,is_debug,content_hash)
        );
        CREATE INDEX idx_native_symbols_expiry ON native_symbols(expires_at);
        ALTER TABLE error_receipts ADD COLUMN native_frames jsonb;
        """);
    public override void Down() => Execute.Sql("ALTER TABLE error_receipts DROP COLUMN native_frames; DROP TABLE native_symbols;");
}
