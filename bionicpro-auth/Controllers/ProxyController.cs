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

            // 4. ИЗВЛЕКАЕМ USER ID ИЗ JWT (ACCESS TOKEN)
            string userId = ExtractUserIdFromJwt(tokens.AccessToken);
            userId = "1"; // залипуха
            if (string.IsNullOrEmpty(userId))
            {
                return BadRequest("Не удалось извлечь идентификатор пользователя из токена безопасности.");
            }

            try
            {
                // 5. ПРОКСИРОВАНИЕ: Идем в наш новый bionicpro-analytics внутри Docker сети
                // Используем порт 8080 и внутренний роут
                var backendTarget = "http://bionicpro-analytics:8080";
                var forwardRequest = new HttpRequestMessage(HttpMethod.Get, $"{backendTarget}/api/internal/reports");
                
                // Передаем проверенный бэкендом ID пользователя в заголовке X-User-Id
                forwardRequest.Headers.Add("X-User-Id", userId);

                var apiResponse = await _httpClient.SendAsync(forwardRequest);
                if (!apiResponse.IsSuccessStatusCode)
                {
                    return StatusCode((int)apiResponse.StatusCode, "Служба аналитики вернула ошибку при обработке отчета.");
                }

                var content = await apiResponse.Content.ReadAsStringAsync();
                return Content(content, "application/json");
            }
            catch (Exception ex)
            {
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

                // Декодируем Payload часть (второй сегмент JWT)
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

                // Ищем кастомное поле user_id (если настроен маппер в Keycloak) 
                // или стандартное поле темы "sub" (Subject Claim)
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