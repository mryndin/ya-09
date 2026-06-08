using System;
using System.Security.Cryptography; // Добавили для SHA256 и RandomNumberGenerator
using System.Text;                  // Добавили для Encoding
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities; // Добавили для WebEncoders
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using BionicProAuth.Models;
using BionicProAuth.Services;

namespace BionicProAuth.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly IKeycloakService _keycloakService;
        private readonly IDistributedCache _cache;
        private readonly IConfiguration _configuration;

        public AuthController(IKeycloakService keycloakService, IDistributedCache cache, IConfiguration configuration)
        {
            _keycloakService = keycloakService;
            _cache = cache;
            _configuration = configuration;
        }

        [HttpGet("login")]
        public IActionResult Login()
        {
            var keycloakUrl = _configuration["Keycloak:Url"];
            var realm = _configuration["Keycloak:Realm"];
            var clientId = _configuration["Keycloak:ClientId"];
            var redirectUri = _configuration["Keycloak:RedirectUri"];

            // 1. Генерируем случайный криптографический code_verifier
            var randomBytes = new byte[32];
            RandomNumberGenerator.Fill(randomBytes);
            var codeVerifier = WebEncoders.Base64UrlEncode(randomBytes);

            // 2. Хэшируем его через SHA-256 для получения code_challenge
            using var sha256 = SHA256.Create();
            var challengeBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(codeVerifier));
            var codeChallenge = WebEncoders.Base64UrlEncode(challengeBytes);

            // 3. Сохраняем верификатор во временную HttpOnly куку (на 30 минут)

            Response.Cookies.Append("pkce_verifier", codeVerifier, new CookieOptions
            {
                HttpOnly = true,
                Secure = false, // вот тут к чертям https
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddMinutes(30)
            });

            // 4. Добавляем параметры code_challenge и метод S256 в URL редиректа
            var authUrl = $"{keycloakUrl}/realms/{realm}/protocol/openid-connect/auth" +
                          $"?client_id={clientId}&response_type=code" +
                          $"&redirect_uri={Uri.EscapeDataString(redirectUri!)}&scope=openid" +
                          $"&code_challenge={codeChallenge}" +
                          $"&code_challenge_method=S256";

            return Redirect(authUrl);
        }

        [HttpGet("callback")]
        public async Task<IActionResult> Callback([FromQuery] string code)
        {
            if (string.IsNullOrEmpty(code)) return BadRequest("Code is missing.");

            // 1. Достаем наш сохраненный pkce_verifier из куки
            if (!Request.Cookies.TryGetValue("pkce_verifier", out var codeVerifier) || string.IsNullOrEmpty(codeVerifier))
            {
                return BadRequest("PKCE verifier is missing or expired.");
            }

            // 2. Сразу удаляем временную куку, она больше не нужна
            Response.Cookies.Delete("pkce_verifier");

            try
            {
                // 3. Передаем code_verifier в сервис обмена токенов (нужно обновить сигнатуру метода!)
                var sessionTokens = await _keycloakService.ExchangeCodeForTokensAsync(code, codeVerifier);
                var sessionId = Guid.NewGuid().ToString();

                var serialized = JsonSerializer.Serialize(sessionTokens);
                await _cache.SetStringAsync(sessionId, serialized, new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
                });

                var cookieOptions = new CookieOptions
                {
                    HttpOnly = true,
                    Secure = false,
                    SameSite = SameSiteMode.Lax,
                    Expires = DateTimeOffset.UtcNow.AddMinutes(30)
                };
                Response.Cookies.Append("BIONIC_SESSION", sessionId, cookieOptions);

                return Redirect(_configuration["Frontend:Url"] ?? "http://localhost:3000/");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal Auth Error: {ex.Message}");
            }
        }

        [HttpGet("status")]
        public async Task<IActionResult> GetStatus()
        {
            if (Request.Cookies.TryGetValue("BIONIC_SESSION", out var sessionId) &&
                !string.IsNullOrEmpty(await _cache.GetStringAsync(sessionId)))
            {
                return Ok(new { isAuthenticated = true });
            }
            return Unauthorized(new { isAuthenticated = false });
        }

        [HttpPost("logout")]
        public async Task<IActionResult> Logout()
        {
            if (Request.Cookies.TryGetValue("BIONIC_SESSION", out var sessionId))
            {
                await _cache.RemoveAsync(sessionId);
                Response.Cookies.Delete("BIONIC_SESSION");
            }
            return Ok();
        }
    }
}