using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

[JsonConverter(typeof(JsonStringEnumConverter<StoreSchemaCode>))]
internal enum StoreSchemaCode { ABSENT, OBSERVED, READBACK_FAILED }
internal sealed record StoreSchemaObservation(StoreSchemaCode Status,int? Version=null,int? NativeError=null);
internal static class StoreSchema
{
    internal const int Current=3;
    // Only a verified installer invokes this mode, with the SCM stopped and
    // maintenance lock held. Never open a second connection to a running service.
    internal static StoreSchemaObservation ObserveOffline()
    {
        try
        {
            ServiceIdentity.RequireStoppedOrAbsent();
            return ObserveFile(StateDirectory.DatabasePath(create:false));
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or Win32Exception or
            InvalidOperationException or System.Security.SecurityException)
        {return new(StoreSchemaCode.READBACK_FAILED,NativeError:e is Win32Exception w?w.NativeErrorCode:e.HResult);}
    }
    internal static StoreSchemaObservation ObserveFile(string path)
    {
        try
        {
            // File.Exists alone suppresses permission errors. Only a real missing
            // path is ABSENT. Present sidecars without a database are ambiguous.
            try{_ = File.GetAttributes(path);}
            catch(Exception e) when(e is FileNotFoundException or DirectoryNotFoundException)
            {
                foreach(string suffix in new[]{"-wal","-shm","-journal"})
                {
                    try{_ = File.GetAttributes(path+suffix);return new(StoreSchemaCode.READBACK_FAILED);}
                    catch(Exception missing) when(missing is FileNotFoundException or DirectoryNotFoundException){}
                }
                return new(StoreSchemaCode.ABSENT);
            }
            using var connection=new SqliteConnection(new SqliteConnectionStringBuilder
                {DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=2}.ToString());
            connection.Open();
            using var command=connection.CreateCommand();
            command.CommandText="PRAGMA user_version";
            long v=(long)command.ExecuteScalar()!;
            return v is >0 and <=int.MaxValue?new(StoreSchemaCode.OBSERVED,(int)v):
                new(StoreSchemaCode.READBACK_FAILED);
        }
        catch(Exception e) when(e is SqliteException or IOException or UnauthorizedAccessException or
            InvalidOperationException or System.Security.SecurityException)
        {return new(StoreSchemaCode.READBACK_FAILED,NativeError:e is SqliteException se?se.SqliteErrorCode:e.HResult);}
    }
}
