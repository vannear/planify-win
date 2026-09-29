using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Planify.Core;

public sealed class LocalStore : IDisposable
{
    private readonly SqliteConnection connection;
    public LocalStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()); connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS state (key TEXT PRIMARY KEY, value TEXT NOT NULL);"; cmd.ExecuteNonQuery();
    }
    public T? Read<T>(string key)
    {
        using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT value FROM state WHERE key=$key"; cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() is string text ? JsonSerializer.Deserialize<T>(text) : default;
    }
    public void Write<T>(string key, T value)
    {
        using var cmd = connection.CreateCommand(); cmd.CommandText = "INSERT INTO state(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$key", key); cmd.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value)); cmd.ExecuteNonQuery();
    }
    public void Dispose() => connection.Dispose();
}
