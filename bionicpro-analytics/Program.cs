using Microsoft.AspNetCore.Mvc;
using System.Data.Common;
using ClickHouse.Client.ADO;

var builder = WebApplication.CreateBuilder(args);

var rawConnString = builder.Configuration.GetConnectionString("ClickHouseConnection")
    ?? "Host=clickhouse;Port=8123;Username=ch_admin;Password=ch_password;Database=default";

// Принудительно меняем ключи от старого драйвера на ключи для нового
var chConnectionString = rawConnString
    .Replace("port=9000", "Port=8123", StringComparison.OrdinalIgnoreCase)
    .Replace("user=", "Username=", StringComparison.OrdinalIgnoreCase); // ВАЖНО: user меняем на Username

// Регистрируем исправленную строку
builder.Services.AddSingleton(chConnectionString);
builder.Services.AddScoped<IReportRepository, ClickHouseReportRepository>();

var app = builder.Build();

app.MapGet("/api/internal/reports", async (
    [FromHeader(Name = "X-User-Id")] string rawUserId, 
    IReportRepository reportRepo) =>
{
    if (string.IsNullOrWhiteSpace(rawUserId) || !ulong.TryParse(rawUserId, out var userId) || userId == 0)
    {
        return Results.BadRequest(new { error = "Невалидный идентификатор." });
    }

    try
    {
        var reports = await reportRepo.GetReportsByUserIdAsync(userId);
        return Results.Ok(reports);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Error] Ошибка: {ex.Message}");
        return Results.Problem("Ошибка при получении аналитических данных.");
    }
});

app.Run();

// --- ДАННЫЕ И РЕПОЗИТОРИЙ ---

public record UserProstheticReportDto(
    ulong UserId, string ReportDate, string ModelName, int StepsCount, 
    float ActiveHours, int BatteryCycles, string LastServiceDate
);

public interface IReportRepository
{
    Task<IEnumerable<UserProstheticReportDto>> GetReportsByUserIdAsync(ulong userId);
}

public class ClickHouseReportRepository : IReportRepository
{
    private readonly string _connectionString;

    public ClickHouseReportRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IEnumerable<UserProstheticReportDto>> GetReportsByUserIdAsync(ulong userId)
    {
        var reports = new List<UserProstheticReportDto>();

        Console.WriteLine("[DIAGNOSTIC] === Попытка подключения через ClickHouse.Client (HTTP) ===");
        Console.WriteLine($"[DIAGNOSTIC] Строка: {_connectionString}");
        
        await using var connection = new ClickHouseConnection(_connectionString);
        await connection.OpenAsync();
        
        Console.WriteLine("[DIAGNOSTIC] Соединение ОТКРЫТО! Выполняю запрос...");

        const string query = @"
            SELECT user_id, report_date, model_name, steps_count, active_hours, battery_cycles, last_service_date 
            FROM default.v_user_prosthetic_reports 
            WHERE user_id = @userId
            ORDER BY report_date DESC";

        await using var command = connection.CreateCommand();
        command.CommandText = query;

        // Создаем параметр стандартным способом ADO.NET
        var param = command.CreateParameter();
        param.ParameterName = "userId";
        param.Value = userId;
        command.Parameters.Add(param);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // Безопасное чтение через универсальный метод GetValue и Convert
            ulong resUserId = reader.IsDBNull(0) ? 0 : Convert.ToUInt64(reader.GetValue(0));
            string resReportDate = reader.IsDBNull(1) ? string.Empty : Convert.ToDateTime(reader.GetValue(1)).ToString("yyyy-MM-dd");
            string resModelName = reader.IsDBNull(2) ? string.Empty : Convert.ToString(reader.GetValue(2)) ?? "";
            int resStepsCount = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3));
            float resActiveHours = reader.IsDBNull(4) ? 0f : Convert.ToSingle(reader.GetValue(4));
            int resBatteryCycles = reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5));
            string resLastServiceDate = reader.IsDBNull(6) ? string.Empty : Convert.ToDateTime(reader.GetValue(6)).ToString("yyyy-MM-dd");

            reports.Add(new UserProstheticReportDto(
                resUserId, resReportDate, resModelName, resStepsCount, 
                resActiveHours, resBatteryCycles, resLastServiceDate));
        }

        Console.WriteLine($"[DIAGNOSTIC] Успешно прочитано строк из ClickHouse: {reports.Count}");
        return reports;
    }
}