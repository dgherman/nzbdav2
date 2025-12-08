using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NzbWebDAV.Database.Interceptors;

public class SqliteForeignKeyEnabler : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
        ";
        command.ExecuteNonQuery();
    }
}