using FluentMigrator;
namespace Aptabase.Data.Migrations;

[Migration(0023)]
public class EraseProcessedSymbolBytes : Migration
{
    public override void Up() => Execute.Sql("UPDATE native_symbols SET content=''::bytea WHERE status<>'pending'");
    public override void Down()
    {
        // This data-only privacy migration cannot reconstruct erased uploads; the schema is unchanged.
    }
}
