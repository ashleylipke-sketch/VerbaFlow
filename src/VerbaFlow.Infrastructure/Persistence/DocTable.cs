using System.Text.Json;

namespace VerbaFlow.Infrastructure.Persistence;

/// <summary>A simple document table: one JSON document per id.</summary>
public sealed class DocTable<T>(SqliteDatabase db, string table)
{
    public async Task<T?> GetAsync(Guid id)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT json FROM {table} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        return await cmd.ExecuteScalarAsync() is string s ? JsonSerializer.Deserialize<T>(s, Json.Options) : default;
    }

    public async Task<List<T>> ListAsync()
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT json FROM {table} ORDER BY rowid";
        var list = new List<T>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(JsonSerializer.Deserialize<T>(r.GetString(0), Json.Options)!);
        return list;
    }

    public async Task UpsertAsync(Guid id, T doc)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT INTO {table}(id, json) VALUES($id, $json) ON CONFLICT(id) DO UPDATE SET json = excluded.json";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(doc, Json.Options));
        await cmd.ExecuteNonQueryAsync();
    }
}
