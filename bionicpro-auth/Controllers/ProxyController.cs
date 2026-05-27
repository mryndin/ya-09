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
            if (!Request.Cookies.TryGetValue("BIONIC_SESSION", out var oldSessionId))
            {
                return Unauthorized("Session cookie explicitly missing.");
            }

            var cachedData = await _cache.GetStringAsync(oldSessionId);
            if (string.IsNullOrEmpty(cachedData))
            {
                return Unauthorized("Session has expired or non-existent.");
            }

            var tokens = JsonSerializer.Deserialize<UserSessionTokens>(cachedData);
            if (tokens == null) return Unauthorized("Session state corrupted.");

            // АВТООБНОВЛЕНИЕ: если 2 минуты истекли
            if (DateTime.UtcNow >= tokens.ExpiresAt)
            {
                try
                {
                    tokens = await _keycloakService.RefreshTokensAsync(tokens.RefreshToken);
                }
                catch
                {
                    await _cache.RemoveAsync(oldSessionId);
                    Response.Cookies.Delete("BIONIC_SESSION");
                    return Unauthorized("Token refresh failed. Re-authentication required.");
                }
            }

            // РОТАЦИЯ СЕССИИ (Session Fixation Countermeasure)
            var newSessionId = Guid.NewGuid().ToString();
            var freshSerialized = JsonSerializer.Serialize(tokens);
            
            // Пишем в кэш под новым ключом
            await _cache.SetStringAsync(newSessionId, freshSerialized, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
            });
            // Чистим старый след
            await _cache.RemoveAsync(oldSessionId);

            // Апдейтим куку клиенту
            var cookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddMinutes(30)
            };
            Response.Cookies.Append("BIONIC_SESSION", newSessionId, cookieOptions);
            Response.Headers.Add("X-New-Session-Id", newSessionId);

            // ПРОКСИРОВАНИЕ: Идем в целевой reports-api за защищенными данными
            var backendTarget = _configuration["BackendApi:Url"] ?? "http://reports-api:5000";
            var forwardRequest = new HttpRequestMessage(HttpMethod.Get, $"{backendTarget}/api/reports");
            forwardRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

            var apiResponse = await _httpClient.SendAsync(forwardRequest);
            if (!apiResponse.IsSuccessStatusCode)
            {
                return StatusCode((int)apiResponse.StatusCode, "Downstream API rejected token.");
            }

            var content = await apiResponse.Content.ReadAsStringAsync();
            return Content(content, "application/json");
        }
    }
}