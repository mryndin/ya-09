using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using BionicProAuth.Models;
using Microsoft.Extensions.Configuration;

namespace BionicProAuth.Services
{
    public class KeycloakService : IKeycloakService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;

        public KeycloakService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _configuration = configuration;
        }

        public async Task<UserSessionTokens> ExchangeCodeForTokensAsync(string code)
        {
            var tokenEndpoint = GetTokenEndpoint();
            var parameters = new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" },
                { "code", code },
                { "redirect_uri", _configuration["Keycloak:RedirectUri"] ?? "" },
                { "client_id", _configuration["Keycloak:ClientId"] ?? "" }
            };

            return await SendTokenRequestAsync(tokenEndpoint, parameters);
        }

        public async Task<UserSessionTokens> RefreshTokensAsync(string refreshToken)
        {
            var tokenEndpoint = GetTokenEndpoint();
            var parameters = new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" },
                { "refresh_token", refreshToken },
                { "client_id", _configuration["Keycloak:ClientId"] ?? "" }
            };

            return await SendTokenRequestAsync(tokenEndpoint, parameters);
        }

        private string GetTokenEndpoint()
        {
            var url = _configuration["Keycloak:Url"];
            var realm = _configuration["Keycloak:Realm"];
            return $"{url}/realms/{realm}/protocol/openid-connect/token";
        }

        private async Task<UserSessionTokens> SendTokenRequestAsync(string endpoint, Dictionary<string, string> parameters)
        {
            var content = new FormUrlEncodedContent(parameters);
            var response = await _httpClient.PostAsync(endpoint, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new Exception($"Keycloak token exchange failed: {response.StatusCode}. Dev-info: {errorContent}");
            }

            var responseString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseString);
            var root = doc.RootElement;

            return new UserSessionTokens
            {
                AccessToken = root.GetProperty("access_token").GetString() ?? string.Empty,
                RefreshToken = root.GetProperty("refresh_token").GetString() ?? string.Empty,
                // Вычитаем 5 секунд дельты на сетевые задержки (токен живет 2 минуты)
                ExpiresAt = DateTime.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32() - 5)
            };
        }
    }
}