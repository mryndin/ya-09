using Microsoft.AspNetCore.Mvc;
using System.Data.Common;
using ClickHouse.Client.ADO;
using ClosedXML.Excel;

var builder = WebApplication.CreateBuilder(args);

var rawConnString = builder.Configuration.GetConnectionString("ClickHouseConnection")
    ?? "Host=clickhouse;Port=8123;Username=ch_admin;Password=ch_password;Database=default";

// Принудительно меняем ключи от старого драйвера на ключи для нового
var chConnectionString = rawConnString
    .Replace("port=9000", "Port=8123", StringComparison.OrdinalIgnoreCase)
    .Replace("user=", "Username=", StringComparison.OrdinalIgnoreCase);

// Регистрируем исправленную строку
builder.Services.AddSingleton(chConnectionString);
builder.Services.AddScoped<IReportRepository, ClickHouseReportRepository>();

var app = builder.Build();

/* Туточки JSON
app.MapGet("/api/internal/reports", async (
    [FromHeader(Name = "X-User-Id")] string rawUserId, 
    IReportRepository reportRepo) =>
{
    // ИСПРАВЛЕНО: Теперь валидируем rawUserId как строку (UUID), а не ulong
    if (string.IsNullOrWhiteSpace(rawUserId))
    {
        return Results.BadRequest(new { error = "Идентификатор пользователя пуст." });
    }

    Console.WriteLine($"[DATA] UserId: {rawUserId.ToLower()}");

    try
    {
        // Передаем строковый UUID дальше в репозиторий
        var reports = await reportRepo.GetReportsByUserIdAsync(rawUserId.ToLower());

        // --- ДОБАВЛЯЕМ ЛОГИРОВАНИЕ ---
        Console.WriteLine($"[DIAGNOSTIC] Отправляем фронтенду {reports.Count()} записей.");
        foreach (var r in reports)
        {
            Console.WriteLine($"[DATA] Date: {r.ReportDate}, Steps: {r.StepsCount}, Model: {r.ModelName}");
        }
        // ------------------------------

        return Results.Ok(reports);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Error] Ошибка: {ex.Message}");
        return Results.Problem("Ошибка при получении аналитических данных.");
    }
});
*/

app.MapGet("/api/internal/reports", async (
    [FromHeader(Name = "X-User-Id")] string rawUserId, 
    IReportRepository reportRepo) =>
{
    try
    {
        // 1. Получаем данные из ClickHouse
        var reports = await reportRepo.GetReportsByUserIdAsync(rawUserId.ToLower());
        
        if (reports == null || !reports.Any())
        {
            return Results.NotFound("Нет данных для формирования отчета.");
        }

        // 2. Генерируем Excel-книгу в памяти
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Активность");

        // Заголовки колонок
        worksheet.Cell(1, 1).Value = "Дата";
        worksheet.Cell(1, 2).Value = "Количество шагов";
        worksheet.Cell(1, 3).Value = "Модель устройства";

        // Стилизуем шапку таблицы
        var headerRow = worksheet.Row(1);
        headerRow.Style.Font.Bold = true;
        headerRow.Style.Fill.BackgroundColor = XLColor.FromHtml("#EAECEF");

        // Заполняем строки данными
        int currentRow = 2;
        foreach (var r in reports)
        {
            worksheet.Cell(currentRow, 1).Value = r.ReportDate;
            worksheet.Cell(currentRow, 1).Style.NumberFormat.Format = "yyyy-mm-dd";
            worksheet.Cell(currentRow, 2).Value = r.StepsCount;
            worksheet.Cell(currentRow, 3).Value = r.ModelName;
            currentRow++;
        }

        // Корректируем ширину колонок под текст
        worksheet.Columns().AdjustToContents();

        // 3. Пишем в поток
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0; // Сбрасываем указатель на начало, чтобы прочитать целиком

        string contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        string fileName = $"report_{DateTime.UtcNow:yyyyMMdd}.xlsx";

        // Отдаем сам поток напрямую. .NET сам закроет stream после отправки.
        return Results.File(
            fileStream: stream, 
            contentType: contentType, 
            fileDownloadName: fileName,
            enableRangeProcessing: false // Отключаем докачку частями, льем одним куском
        );
    }
    catch (Exception ex)
    {
        return Results.Problem($"Ошибка генерации отчета: {ex.Message}");
    }
});

app.Run();

// --- ДАННЫЕ И РЕПОЗИТОРИЙ ---

// ИСПРАВЛЕНО: UserId теперь имеет тип string для хранения UUID
public record UserProstheticReportDto(
    string UserId, string ReportDate, string ModelName, int StepsCount, 
    float ActiveHours, int BatteryCycles, string LastServiceDate
);

public interface IReportRepository
{
    Task<IEnumerable<UserProstheticReportDto>> GetReportsByUserIdAsync(string userId);
}

public class ClickHouseReportRepository : IReportRepository
{
    private readonly string _connectionString;

    public ClickHouseReportRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IEnumerable<UserProstheticReportDto>> GetReportsByUserIdAsync(string userId)
    {
        var reports = new List<UserProstheticReportDto>();

        await using var connection = new ClickHouseConnection(_connectionString);
        await connection.OpenAsync();

        // ИСПРАВЛЕНО: Приводим обе стороны сравнения к нижнему регистру в SQL
        const string query = @"
        SELECT toString(user_guid) as user_guid, report_date, model_name, steps_count, active_hours, battery_cycles, last_service_date 
        FROM default.v_user_prosthetic_reports 
        WHERE lower(toString(user_guid)) = lower(@userId)
        ORDER BY report_date DESC";

        await using var command = connection.CreateCommand();
        command.CommandText = query;

        var param = command.CreateParameter();
        param.ParameterName = "userId";
        param.Value = userId; // Передаем как есть
        command.Parameters.Add(param);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // ИСПРАВЛЕНО: Чтение первого поля как string (UUID)
            string resUserId = reader.IsDBNull(0) ? string.Empty : Convert.ToString(reader.GetValue(0)) ?? "";
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