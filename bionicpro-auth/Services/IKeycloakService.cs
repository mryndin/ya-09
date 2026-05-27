using System.Threading.Tasks;
using BionicProAuth.Models;

namespace BionicProAuth.Services
{
    public interface IKeycloakService
    {
        Task<UserSessionTokens> ExchangeCodeForTokensAsync(string code);
        Task<UserSessionTokens> RefreshTokensAsync(string refreshToken);
    }
}