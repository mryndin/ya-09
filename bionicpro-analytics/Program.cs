using ClickHouse.Client.ADO;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Bibliography;
using Microsoft.AspNetCore.Mvc;
using System.Data.Common;

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

app.MapGet("/api/internal/reports", async (
    [FromHeader(Name = "X-User-Id")] string rawUserId,
    [FromQuery] DateTime? fromDate,
    [FromQuery] DateTime? toDate,
    IReportRepository reportRepo) =>
{
    try
    {
        // Предусматриваем генерацию только за обработанный период.
        // Airflow считает данные максимум за вчерашний день, поэтому запрещаем брать "сегодня" и будущее.
        var maxAllowedDate = DateTime.UtcNow.Date.AddDays(-1);

        // Если даты не переданы с фронтенда, ставим дефолт (например, последние 30 дней)
        var finalFromDate = fromDate ?? DateTime.UtcNow.Date.AddDays(-30);
        var finalToDate = toDate ?? maxAllowedDate;

        // Жесткая проверка безопасности: если пользователь пытается запросить необработанный период
        if (finalToDate > maxAllowedDate)
        {
            finalToDate = maxAllowedDate; // Срезаем до максимально доступного
        }

        if (finalFromDate > finalToDate)
        {
            return Results.BadRequest("Некорректный период: начальная дата не может быть больше конечной или превышать обработанный Airflow период.");
        }

        // Передаем даты в репозиторий для фильтрации в SQL/ClickHouse запросе
        var reports = await reportRepo.GetReportsByUserIdAndPeriodAsync(rawUserId.ToLower(), finalFromDate, finalToDate);

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
    // Ваш старый метод (если он остался)
    Task<IEnumerable<UserProstheticReportDto>> GetReportsByUserIdAsync(string userId);

    // ДОБАВЬТЕ ЭТУ СТРОКУ:
    Task<IEnumerable<UserProstheticReportDto>> GetReportsByUserIdAndPeriodAsync(string userId, DateTime fromDate, DateTime toDate);
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

    public async Task<IEnumerable<UserProstheticReportDto>> GetReportsByUserIdAndPeriodAsync(string userId, DateTime fromDate, DateTime toDate)
    {
        var reports = new List<UserProstheticReportDto>();

        await using var connection = new ClickHouseConnection(_connectionString);
        await connection.OpenAsync();

        const string query = @"
        SELECT 
            toString(user_guid) as user_guid, 
            report_date, 
            model_name, 
            steps_count, 
            active_hours, 
            battery_cycles, 
            last_service_date 
        FROM default.v_user_prosthetic_reports 
        WHERE lower(toString(user_guid)) = lower({userId:String})
          AND report_date >= {fromDate:Date}
          AND report_date <= {toDate:Date}
        ORDER BY report_date DESC";

        await using var command = connection.CreateCommand();
        command.CommandText = query;

        // 1. Параметр пользователя
        var userIdParam = command.CreateParameter();
        userIdParam.ParameterName = "userId";
        userIdParam.Value = userId.ToLower();
        command.Parameters.Add(userIdParam);

        // 2. Параметр даты С (fromDate) — ПЕРЕДАЕМ СТРОКОЙ 10 БАЙТ
        var fromDateParam = command.CreateParameter();
        fromDateParam.ParameterName = "fromDate";
        fromDateParam.Value = fromDate.ToString("yyyy-MM-dd"); // <--- Форматируем в YYYY-MM-DD
        command.Parameters.Add(fromDateParam);

        // 3. Параметр даты ПО (toDate) — ПЕРЕДАЕМ СТРОКОЙ 10 БАЙТ
        var toDateParam = command.CreateParameter();
        toDateParam.ParameterName = "toDate";
        toDateParam.Value = toDate.ToString("yyyy-MM-dd"); // <--- Форматируем в YYYY-MM-DD
        command.Parameters.Add(toDateParam);

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