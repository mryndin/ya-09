using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using BionicProAuth.Models;
using BionicProAuth.Services;

namespace BionicProAuth.Controllers
{
    [ApiController]
    [Route("api/proxy")]
    public class ProxyController : ControllerBase
    {
        private readonly IDistributedCache _cache;
        private readonly IKeycloakService _keycloakService;
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;

        public ProxyController(IDistributedCache cache, IKeycloakService keycloakService, HttpClient httpClient, IConfiguration configuration)
        {
            _cache = cache;
            _keycloakService = keycloakService;
            _httpClient = httpClient;
            _configuration = configuration;
        }

        [HttpGet("reports")]
        public async Task<IActionResult> GetReports()
        {
            // 1. Проверяем наличие куки сессии BFF
            if (!Request.Cookies.TryGetValue("BIONIC_SESSION", out var oldSessionId))
            {
                return Unauthorized("Session cookie explicitly missing.");
            }

            // 2. Достаем токены из кэша RAM
            var cachedData = await _cache.GetStringAsync(oldSessionId);
            if (string.IsNullOrEmpty(cachedData))
            {
                return Unauthorized("Session expired or invalid.");
            }

            var tokens = JsonSerializer.Deserialize<UserSessionTokens>(cachedData);
            if (tokens == null || string.IsNullOrEmpty(tokens.AccessToken))
            {
                return Unauthorized("Invalid token structure in session.");
            }

            // 3. Проверка протухания токена Keycloak и его автоматическое обновление
            if (DateTime.UtcNow >= tokens.ExpiresAt)
            {
                try
                {
                    tokens = await _keycloakService.RefreshTokensAsync(tokens.RefreshToken);
                }
                catch (Exception ex)
                {
                    await _cache.RemoveAsync(oldSessionId);
                    Response.Cookies.Delete("BIONIC_SESSION");
                    return Unauthorized($"Session refresh failed: {ex.Message}");
                }

                var newSessionId = Guid.NewGuid().ToString();
                var serialized = JsonSerializer.Serialize(tokens);
                await _cache.SetStringAsync(newSessionId, serialized, new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
                });
                await _cache.RemoveAsync(oldSessionId);

                var cookieOptions = new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    Expires = DateTimeOffset.UtcNow.AddMinutes(30)
                };
                Response.Cookies.Append("BIONIC_SESSION", newSessionId, cookieOptions);
                Response.Headers.Add("X-New-Session-Id", newSessionId);
            }

            // 4. Извлекаем User ID из JWT (Access Token)
            string userId = ExtractUserIdFromJwt(tokens.AccessToken);
            if (string.IsNullOrEmpty(userId))
            {
                return BadRequest("Не удалось извлечь идентификатор пользователя из токена безопасности.");
            }

            try
            {
                // 5. Проксирование запроса к аналитике
                var backendTarget = "http://bionicpro-analytics:8080";

                // Проверяем QueryString на пустоту и корректность
                var queryString = Request.QueryString.HasValue ? Request.QueryString.Value : string.Empty;

                // Защита от мусорных символов, ломающих эндпоинты аналитики
                if (queryString == "?" || queryString == "?*")
                {
                    queryString = string.Empty;
                }

                var targetUrl = $"{backendTarget}/api/internal/reports{queryString}";

                // Логируем исходящий запрос для отладки в консоли bionicpro-auth
                Console.WriteLine($"[BFF Proxy] Forwarding metadata request to: {targetUrl} for User-Id: {userId}");

                var forwardRequest = new HttpRequestMessage(HttpMethod.Get, targetUrl);
                forwardRequest.Headers.Add("X-User-Id", userId);

                // Оптимизация: Считываем только заголовки на старте
                var apiResponse = await _httpClient.SendAsync(forwardRequest, HttpCompletionOption.ResponseHeadersRead);

                if (!apiResponse.IsSuccessStatusCode)
                {
                    // Вычитываем точную ошибку из аналитики (например, "Нет данных за указанный период")
                    var errorDetails = await apiResponse.Content.ReadAsStringAsync();
                    Console.WriteLine($"[BFF Proxy ERROR] Analytics service returned {apiResponse.StatusCode}. Details: {errorDetails}");

                    return StatusCode((int)apiResponse.StatusCode, $"Служба аналитики вернула ошибку: {errorDetails}");
                }

                // ИСПРАВЛЕНО: Теперь считываем текстовый JSON-ответ со ссылкой на CDN, а не тяжелый стрим файла
                var jsonContent = await apiResponse.Content.ReadAsStringAsync();

                // Возвращаем JSON фронтенду с корректным заголовком контента
                return Content(jsonContent, "application/json");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BFF Proxy Critical Exception] {ex.Message}");
                return StatusCode(500, $"Ошибка сетевого взаимодействия со службой аналитики: {ex.Message}");
            }
        }

        /// <summary>
        /// Легковесный и безопасный парсинг JWT-токена без внешних библиотек
        /// </summary>
        private string ExtractUserIdFromJwt(string accessToken)
        {
            try
            {
                var parts = accessToken.Split('.');
                if (parts.Length < 2) return string.Empty;

                var payload = parts[1];
                payload = payload.Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }

                var bytes = Convert.FromBase64String(payload);
                var json = System.Text.Encoding.UTF8.GetString(bytes);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("user_id", out var userIdProp))
                {
                    return userIdProp.ToString();
                }

                if (root.TryGetProperty("sub", out var subProp))
                {
                    return subProp.GetString() ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JWT Parsing Error] {ex.Message}");
            }

            return string.Empty;
        }
    }
}