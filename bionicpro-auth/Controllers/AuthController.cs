using System;
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

            var authUrl = $"{keycloakUrl}/realms/{realm}/protocol/openid-connect/auth" +
                          $"?client_id={clientId}&response_type=code" +
                          $"&redirect_uri={Uri.EscapeDataString(redirectUri!)}&scope=openid";

            return Redirect(authUrl);
        }

        [HttpGet("callback")]
        public async Task<IActionResult> Callback([FromQuery] string code)
        {
            if (string.IsNullOrEmpty(code)) return BadRequest("Code is missing.");

            try
            {
                var sessionTokens = await _keycloakService.ExchangeCodeForTokensAsync(code);
                var sessionId = Guid.NewGuid().ToString();

                var serialized = JsonSerializer.Serialize(sessionTokens);
                await _cache.SetStringAsync(sessionId, serialized, new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) // Сессия живет дольше access_token
                });

                var cookieOptions = new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true, // Защита от перехвата в незащищенном трафике
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