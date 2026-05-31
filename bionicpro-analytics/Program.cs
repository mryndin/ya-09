using Amazon.S3;
using Amazon.S3.Model;
using ClickHouse.Client.ADO;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using System.Data.Common;

var builder = WebApplication.CreateBuilder(args);

var rawConnString = builder.Configuration.GetConnectionString("ClickHouseConnection")
    ?? "Host=clickhouse;Port=8123;Username=ch_admin;Password=ch_password;Database=default";

// Принудительно меняем ключи от старого драйвера на ключи для нового
var chConnectionString = rawConnString
    .Replace("port=9000", "Port=8123", StringComparison.OrdinalIgnoreCase)
    .Replace("user=", "Username=", StringComparison.OrdinalIgnoreCase);

// Регистрируем исправленную строку подключения и репозиторий
builder.Services.AddSingleton(chConnectionString);
builder.Services.AddScoped<IReportRepository, ClickHouseReportRepository>();

// Регистрируем клиент Amazon S3. 
// Внутри Docker-сети аналитика видит MinIO по имени сервиса и внутреннему порту 9005
builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var config = new AmazonS3Config
    {
        ServiceURL = "http://minio:9000",
        ForcePathStyle = true // Обязательно для корректной работы с MinIO/Ceph
    };

    return new AmazonS3Client("minio_admin", "minio_secure_password", config);
});

var app = builder.Build();

// ИСПРАВЛЕНО: Теперь эндпоинт возвращает JSON (IResult) со ссылкой на CDN вместо бинарного потока
app.MapGet("/api/internal/reports", async (
    [FromHeader(Name = "X-User-Id")] string rawUserId,
    [FromQuery] DateTime? fromDate,
    [FromQuery] DateTime? toDate,
    IReportRepository reportRepo,
    IAmazonS3 s3Client) =>
{
    try
    {
        // Предусматриваем генерацию только за обработанный период.
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
            return Results.BadRequest("Некорректный период: начальная дата не может быть больше конечной.");
        }

        string bucketName = "bionicpro-reports";

        // 1. Файл должен физически лежать в папке reports/ внутри бакета MinIO
        string s3Key = $"reports/{rawUserId.ToLower()}/report_{finalFromDate:yyyyMMdd}_to_{finalToDate:yyyyMMdd}.xlsx";

        // 2. Внешний адрес CDN
        string cdnBaseUrl = "http://localhost:8082";

        // 3. Ссылка для фронтенда (совпадает с вашей работающей ссылкой)
        string cdnUrl = $"{cdnBaseUrl}/{s3Key}";

        bool fileExists = false;
        try
        {
            // Быстрая проверка: запрашиваем только метаданные объекта в S3, не скачивая сам файл
            await s3Client.GetObjectMetadataAsync(bucketName, s3Key);
            fileExists = true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            fileExists = false;
        }

        // ==========================================
        // КЭШ-ХИТ: Файл уже есть в S3
        // ==========================================
        if (fileExists)
        {
            Console.WriteLine($"[Cache HIT] Отчет найден в S3: {s3Key}. Возвращаем URL к CDN.");
            return Results.Ok(new { url = cdnUrl, fromCache = true });
        }

        // ==========================================
        // КЭШ-МИСС: Файла нет -> Считываем ClickHouse и генерируем
        // ==========================================
        Console.WriteLine($"[Cache MISS] Отчет отсутствует в S3. Запрашиваем ClickHouse для: {s3Key}");

        var reports = await reportRepo.GetReportsByUserIdAndPeriodAsync(rawUserId.ToLower(), finalFromDate, finalToDate);

        if (reports == null || !reports.Any())
        {
            return Results.NotFound("Нет данных для формирования отчета.");
        }

        // Генерируем Excel-книгу в памяти
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Активность");

        // Заголовки колонок
        worksheet.Cell(1, 1).Value = "Дата";
        worksheet.Cell(1, 2).Value = "Количество шагов";
        worksheet.Cell(1, 3).Value = "Модель устройства";

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

        worksheet.Columns().AdjustToContents();

        // Пишем в MemoryStream для отправки в хранилище
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        // Загружаем готовый сгенерированный файл в MinIO бакет
        var putRequest = new PutObjectRequest
        {
            BucketName = bucketName,
            Key = s3Key,
            InputStream = stream,
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        };
        await s3Client.PutObjectAsync(putRequest);
        Console.WriteLine($"[S3 Upload] Новый отчет успешно сохранен в S3: {s3Key}");

        // Возвращаем JSON со ссылкой на проксирование через CDN
        return Results.Ok(new { url = cdnUrl, fromCache = false });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Ошибка обработки или генерации отчета: {ex.Message}");
    }
});

app.Run();

// --- ДАННЫЕ И РЕПОЗИТОРИЙ ---

public record UserProstheticReportDto(
    string UserId, string ReportDate, string ModelName, int StepsCount,
    float ActiveHours, int BatteryCycles, string LastServiceDate
);

public interface IReportRepository
{
    Task<IEnumerable<UserProstheticReportDto>> GetReportsByUserIdAsync(string userId);
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

        // ИСПРАВЛЕНО: Читаем из новой CDC-витрины с использованием FINAL для получения актуальных версий
        const string query = @"
        SELECT toString(user_guid) as user_guid, report_date, model_name, steps_count, active_hours, battery_cycles, last_service_date 
        FROM default.v_user_prosthetic_reports_cdc FINAL
        WHERE lower(toString(user_guid)) = lower(@userId)
        ORDER BY report_date DESC";

        await using var command = connection.CreateCommand();
        command.CommandText = query;

        var param = command.CreateParameter();
        param.ParameterName = "userId";
        param.Value = userId;
        command.Parameters.Add(param);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
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

        return reports;
    }

    public async Task<IEnumerable<UserProstheticReportDto>> GetReportsByUserIdAndPeriodAsync(string userId, DateTime fromDate, DateTime toDate)
    {
        var reports = new List<UserProstheticReportDto>();

        await using var connection = new ClickHouseConnection(_connectionString);
        await connection.OpenAsync();

        // ИСПРАВЛЕНО: Массовая выгрузка за период переведена на CDC-таблицу
        const string query = @"
        SELECT 
            toString(user_guid) as user_guid, 
            report_date, 
            model_name, 
            steps_count, 
            active_hours, 
            battery_cycles, 
            last_service_date 
        FROM default.v_user_prosthetic_reports_cdc FINAL
        WHERE lower(toString(user_guid)) = lower({userId:String})
          AND report_date >= {fromDate:Date}
          AND report_date <= {toDate:Date}
        ORDER BY report_date DESC";

        await using var command = connection.CreateCommand();
        command.CommandText = query;

        var userIdParam = command.CreateParameter();
        userIdParam.ParameterName = "userId";
        userIdParam.Value = userId.ToLower();
        command.Parameters.Add(userIdParam);

        var fromDateParam = command.CreateParameter();
        fromDateParam.ParameterName = "fromDate";
        fromDateParam.Value = fromDate.ToString("yyyy-MM-dd");
        command.Parameters.Add(fromDateParam);

        var toDateParam = command.CreateParameter();
        toDateParam.ParameterName = "toDate";
        toDateParam.Value = toDate.ToString("yyyy-MM-dd");
        command.Parameters.Add(toDateParam);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
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

        Console.WriteLine($"[CDC OLAP] Прочитано строк из CDC-витрины ClickHouse: {reports.Count}");
        return reports;
    }
}