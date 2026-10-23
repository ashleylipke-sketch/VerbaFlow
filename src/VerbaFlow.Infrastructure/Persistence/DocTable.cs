using System.Text.Json;

namespace VerbaFlow.Infrastructure.Persistence;

/// <summary>A simple document table: one JSON document per id.</summary>
public sealed class DocTable<T>(Database db, string table)
{
    public async Task<T?> GetAsync(Guid id)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT json FROM {table} WHERE id = @id";
        cmd.Add("@id", id.ToString());
        return await cmd.ExecuteScalarAsync() is string s ? JsonSerializer.Deserialize<T>(s, Json.Options) : default;
    }

    public async Task<List<T>> ListAsync()
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT json FROM {table} ORDER BY {db.InsertOrder}";
        var list = new List<T>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(JsonSerializer.Deserialize<T>(r.GetString(0), Json.Options)!);
        return list;
    }

    public async Task UpsertAsync(Guid id, T doc)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT INTO {table}(id, json) VALUES(@id, @json) ON CONFLICT(id) DO UPDATE SET json = excluded.json";
        cmd.Add("@id", id.ToString());
        cmd.Add("@json", JsonSerializer.Serialize(doc, Json.Options));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        await using var c = db.Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table} WHERE id = @id";
        cmd.Add("@id", id.ToString());
        return await cmd.ExecuteNonQueryAsync() > 0;
    }
}
